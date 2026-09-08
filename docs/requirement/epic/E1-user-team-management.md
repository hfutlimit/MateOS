# E1 · User & Team Management

| 字段 | 值 |
| --- | --- |
| Epic ID | E1 |
| 标题 | User & Team Management |
| 阶段 | MVP（M1） |
| 上游 | PRD v0.3 §4 / SYSTEM_DESIGN v0.2 §3 auth/iam + org/team/project |
| 下游 | E2 / E3 / E4 / E5 / E6 / E8 全部依赖 |
| 状态 | Draft |

## 1. 背景与动机

MateOS 是多租户协作系统，所有协作必须挂在**人**身上——没有人类成员就无法邀请 Agent、没有 Project 就没有知识边界。E1 把"组织 → 团队 → 项目"三层骨架搭起来，作为后续所有 epic 的承载层。

核心原则：
- **Human User 是事实源头**：User ID 出现在 owner_user_id / created_by / approved_by / actor_id 所有审计字段
- **Project = 知识边界**（PRD §3.2）：Agent 能看到什么，由 Project 决定，不由 Channel 决定
- **MVP 不做企业 IAM/SSO**（PRD §8 Non Goals）

## 2. 范围

### 2.1 In Scope

- 用户注册（email + password + display_name）
- 用户登录（JWT access + refresh token，access 15min / refresh 30d）
- Token 刷新与登出（黑名单 / Redis revoke）
- Organization / Team / Project 三级 CRUD
- Project 成员邀请（Human / Agent slot 占位，Agent slot 由 E2 填充）
- Owner / Member 角色（Project 级；Team 后续扩展）
- 个人资料查看 / 修改（display_name、avatar URL、password 修改）

### 2.2 Out of Scope

- 邮箱验证、找回密码（V2）
- 多因素认证（V2）
- 企业 SSO / OIDC / SAML（V1 Non Goal）
- 跨组织 Project 共享（V2+）
- Team 内多 Owner 协商（V1 单 Owner）

## 3. 数据模型

```sql
-- users
CREATE TABLE users (
  id              UUID PRIMARY KEY,
  email           CITEXT UNIQUE NOT NULL,
  password_hash   TEXT NOT NULL,        -- argon2id
  display_name    TEXT NOT NULL,
  avatar_url      TEXT,
  created_at      TIMESTAMPTZ DEFAULT now(),
  updated_at      TIMESTAMPTZ DEFAULT now()
);

-- refresh_tokens (Redis: rt:{user_id}:{jti}, TTL 30d)
-- organizations
CREATE TABLE organizations (
  id          UUID PRIMARY KEY,
  name        TEXT NOT NULL,
  owner_id    UUID NOT NULL REFERENCES users(id),
  created_at  TIMESTAMPTZ DEFAULT now()
);

-- teams
CREATE TABLE teams (
  id          UUID PRIMARY KEY,
  org_id      UUID NOT NULL REFERENCES organizations(id),
  name        TEXT NOT NULL,
  created_at  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_teams_org ON teams(org_id);

-- team_members
CREATE TABLE team_members (
  team_id     UUID NOT NULL REFERENCES teams(id),
  user_id     UUID NOT NULL REFERENCES users(id),
  role        TEXT NOT NULL CHECK (role IN ('owner','member')),
  joined_at   TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (team_id, user_id)
);

-- projects (知识边界)
CREATE TABLE projects (
  id                UUID PRIMARY KEY,
  team_id           UUID NOT NULL REFERENCES teams(id),
  name              TEXT NOT NULL,
  description       TEXT,
  repo_url          TEXT,
  -- v0.2 起：外部集成扩展点（§10）
  integration_backend  TEXT NOT NULL DEFAULT 'mateos'
                       CHECK (integration_backend IN ('mateos','agentboard')),
  issue_tracker        TEXT NOT NULL DEFAULT 'none'
                       CHECK (issue_tracker IN ('none','agentboard','jira')),
  -- 关联配置
  issue_tracker_meta   JSONB,           -- Jira OAuth token、Project key 等
  created_at        TIMESTAMPTZ DEFAULT now(),
  updated_at        TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_projects_team ON projects(team_id);

-- project_members
CREATE TABLE project_members (
  project_id    UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  user_id       UUID NOT NULL REFERENCES users(id),
  role          TEXT NOT NULL CHECK (role IN ('owner','member')),
  joined_at     TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (project_id, user_id)
);
```

### 3.1 字段约束与不变量

- `organizations.owner_id` 必须为 `team_members` 中 role='owner' 的成员
- `projects` 删除时级联清理 `project_members` / `channels`（E3）/ `memory_items`（E5）
- `email` 唯一，**软删不复活**（用 `deleted_at`，但 MVP 不做软删）

## 4. API

### 4.1 REST（`/api/v1`）

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| POST | `/auth/register` | 注册（返回 access + refresh） | 公开 |
| POST | `/auth/login` | 登录 | 公开 |
| POST | `/auth/refresh` | 刷新 access token | refresh token |
| POST | `/auth/logout` | 登出（吊销 refresh） | login |
| GET | `/me` | 当前用户 | login |
| PATCH | `/me` | 修改 display_name / avatar | login |
| POST | `/me/password` | 改密（需旧密码） | login |
| POST | `/orgs` | 创建 org | login |
| GET | `/orgs/:id` | 查看 org | member |
| POST | `/teams` | 创建 team | org member |
| GET | `/teams/:id` | 查看 team | team member |
| POST | `/teams/:id/members` | 邀请成员 | team owner |
| DELETE | `/teams/:id/members/:uid` | 移除成员 | team owner |
| POST | `/projects` | 创建 project | team member |
| GET | `/projects/:id` | 查看 project | project member |
| PATCH | `/projects/:id` | 修改 name / desc / integration 配置 | project owner |
| DELETE | `/projects/:id` | 删除（级联） | project owner |
| POST | `/projects/:id/members` | 邀请 Human/Agent slot | project owner |
| GET | `/projects/:id/members` | 列出成员 | project member |

### 4.2 WebSocket

- 无（本 epic 全部 REST；WS 由 E3 引入）

### 4.3 错误码

| HTTP | 含义 |
| --- | --- |
| 400 | 入参校验失败（具体字段 error.details） |
| 401 | 未登录 / token 无效 |
| 403 | 权限不足（subject 不在 scope 内） |
| 404 | 资源不存在 |
| 409 | email 已注册 / 已是 member / 角色冲突 |
| 429 | 登录限流（5/min，per email） |

## 5. 关键流程

### 5.1 注册 → 创建 Project → 邀请成员

```
1. POST /auth/register {email, password, display_name}
   → user 创建、argon2id 哈希、JWT 双令牌签发
   → 写 audit_logs(action=USER_REGISTERED)

2. POST /orgs {name}
   → 创建 org，owner = 当前 user
   → 自动写 teams(MVP: 一个 org 一个 default team "General")

3. POST /projects {team_id, name, description, repo_url}
   → 创建 project
   → 自动写 project_members(user_id, role='owner')

4. POST /projects/:id/members {user_id, role='member'}  或 {agent_id, ...}
   → Human：直接写入
   → Agent slot：先占位，E2 创建 Agent 后回填
   → WS 事件 presence.updated 推送给所有 project 在线成员（E7）
```

### 5.2 登录限流

```
Redis rl:{email_or_ip}:/auth/login
  Sorted Set(score=now, member=request_id)
  滑窗 5/min，过期 ZREMRANGEBYSCORE
```

### 5.3 Token 刷新轮转

```
POST /auth/refresh {refresh_token}
  → 校验 jti ∈ Redis rt:{user_id}:{jti}
  → 吊销旧 jti（DEL）
  → 签发新 jti，Redis 重置 TTL 30d
  → 返回新 access + refresh
```

## 6. UI

### 6.1 页面

| 页面 | 原型 | 状态 |
| --- | --- | --- |
| P1 登录 / 注册 | 待做 | P1 |
| P8 团队与组织设置 | 待做 | P1 |

### 6.2 组件

- `<UserAvatar>`：26px 圆角 6px（人类），hover 展示 display_name
- `<TeamSelector>`：顶栏下拉，列出当前用户所有 team + 当前选中
- `<ProjectSwitcher>`：侧栏顶部，列出当前 team 的 project，current 用主色描边

### 6.3 状态

- 注册按钮 loading（spinner + "注册中..."）
- 邀请失败：toast（"该邮箱已注册" / "无权限"）
- 删除 Project：二次确认 modal（"将级联删除所有频道、消息、记忆"）

## 7. 验收标准

### 7.1 功能

- **F1** 注册 → 登录 → 拿到 access + refresh token（access 15min 后过期）
- **F2** refresh token 轮转：旧 jti 立即失效，新 jti 30d 有效
- **F3** 创建 Project 后刷新页面，列表中可见
- **F4** 非 project member 调用 `/projects/:id` 返回 403
- **F5** 删除 Project 级联删除 channels / messages / memory（E3 / E5 实现后联动验证）
- **F6** email 唯一性：重复注册返回 409
- **F7** 登录限流：1 分钟内 6 次同邮箱登录，第 6 次返回 429

### 7.2 端到端（E2E）

- `e2e/E1-001-register-login`：注册→登录→`/me` 返回正确 display_name
- `e2e/E1-002-project-lifecycle`：创建→修改→邀请→删除，成员列表同步
- `e2e/E1-003-refresh-rotation`：access 过期→refresh 拿新 access→旧 access 拒绝
- `e2e/E1-004-permission-isolation`：非 member 调 `/projects/:id` 返 403
- `e2e/E1-005-rate-limit`：6 次/min 触发限流

### 7.3 非功能

- 注册 P95 < 300ms（含 argon2id）
- `/me` P95 < 50ms（带 Redis 缓存）
- 1000 并发登录场景下无 5xx（限流后）

## 8. 与其他 Epic 的关系

- **被依赖**：
  - E2 Agent 创建设置 `owner_user_id`（必备）
  - E3 Channel 创建必填 `project_id`（必备）
  - E4 Decision/Mention `sender_id` 引用
  - E5 Memory `owner_user_id` / `approved_by` 引用
  - E6 Permission `subject_id` 引用
  - E8 Audit logs `actor_id` 引用
- **依赖**：无（最底层）
- **冲突裁决**：与 PRD §4.1 / SYSTEM_DESIGN §3.1 auth/iam 模块对齐

## 9. 风险与开放问题

- **R1**：JWT secret rotation 策略（V1 用单一 secret；V2 引入 kid 轮换）
- **R2**：org / team 层级在 V1 简化（org→team 一对多），是否在 E1 就支持多 owner？→ **否，单 owner，V2 扩展**
- **R3**：avatar 走外链 URL 还是 S3 直传？→ MVP 外链，V2 接 S3（与 E3 附件同链路）

## 10. 实施顺序（M1）

1. `apps/api` NestJS 项目脚手架 + Prisma + PG schema
2. `/auth/*` 四个端点 + JWT 签发 + Redis rt 存储
3. `/orgs` `/teams` `/projects` CRUD + member 关系
4. `/me` profile 修改 + 改密
5. E2E 测试套件（pytest，5 个 case）
6. 前端 P1 / P8 页面（待做，MVP 期间可后置）
