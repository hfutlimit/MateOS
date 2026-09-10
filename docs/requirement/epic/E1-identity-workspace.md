# E1 · Identity & Workspace

| 字段 | 值 |
| --- | --- |
| Epic ID | E1 |
| 标题 | Identity & Workspace |
| 阶段 | S1 前置（能力域 M1） |
| 上游 | PRD v0.4 §4 / SYSTEM_DESIGN v0.9 §3 auth/iam + org/team/project |
| 下游 | E2 / E3 / E4 / E5 / E6 / E7 / E8 / E10 全部依赖 |
| 状态 | Draft（v0.9 同步） |

## 1. 背景与动机

E1 是整个 MateOS 的承载层：Org → Team → Project 三级骨架 + 成员关系。所有其他 Epic 都挂在 Project / Member 上。本 epic：

- **v0.4 新增** `organization_members` 表（之前只建模到 `team_members`，Org member 没有事实源）
- **v0.4 新增** Org / Team / Project 三层 owner 独立（不再"Org owner 必须是某 Team owner"这种不变量）
- **v0.4 移除** `projects.integration_backend` / `projects.issue_tracker` 字段（v0.3 的 Work Management 错误把 Provider 字段挂在 Project 表；v0.4 改用 `work_item_bindings` 表）

## 2. 范围

### 2.1 In Scope

- 用户注册 / 登录 / JWT（access 15min + refresh 30d）
- Token 刷新轮转 + 登出吊销
- 个人资料（display_name / avatar）
- Organization CRUD（org owner 独立角色）
- **v0.4 新增** Organization membership（多 owner 独立角色）
- Team CRUD + team_members
- Project CRUD + project_members（**无** provider 字段）
- Member 邀请（Human / Agent slot 占位）
- 三层 owner：org owner / team owner / project owner 各自独立

### 2.2 Out of Scope

- 邮箱验证、找回密码（V2）
- 企业 IAM / SSO（V1 Non Goal）
- 跨组织 Project 共享（V2+）
- 团队内多 owner 协商（V1 单 owner，V2 扩展）

## 3. 数据模型

```sql
CREATE TABLE users (
  id              UUID PRIMARY KEY,
  email           CITEXT UNIQUE NOT NULL,
  password_hash   TEXT NOT NULL,                  -- argon2id
  display_name    TEXT NOT NULL,
  avatar_url      TEXT,
  created_at      TIMESTAMPTZ DEFAULT now()
);

-- v0.4.2 改：删除 organizations.owner_id（双事实源）
-- 真正权限事实源：organization_members.role = 'owner'
-- 如果想记录创建人，加 created_by 字段（信息性，不影响权限）
CREATE TABLE organizations (
  id          UUID PRIMARY KEY,
  name        TEXT NOT NULL,
  created_by  UUID REFERENCES users(id),  -- v0.4.2 改：仅信息性，不参与权限判定
  created_at  TIMESTAMPTZ DEFAULT now()
);

-- v0.4 新增 / v0.4.2 改：Org 成员是 owner 角色的**唯一事实源**
CREATE TABLE organization_members (
  organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
  user_id         UUID NOT NULL REFERENCES users(id),
  role            TEXT NOT NULL CHECK (role IN ('owner','member')),
  joined_at       TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (organization_id, user_id)
);
-- v0.4.2 改：至少 1 个 owner 约束（DB 层 invariant，可延迟到 V2）

CREATE TABLE teams (
  id          UUID PRIMARY KEY,
  org_id      UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
  name        TEXT NOT NULL,
  created_at  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_teams_org ON teams(org_id);

CREATE TABLE team_members (
  team_id     UUID NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL REFERENCES users(id),
  role        TEXT NOT NULL CHECK (role IN ('owner','member')),
  joined_at   TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (team_id, user_id)
);

-- v0.4 简化：projects 不再含 integration_backend / issue_tracker
CREATE TABLE projects (
  id          UUID PRIMARY KEY,
  team_id     UUID NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
  name        TEXT NOT NULL,
  description TEXT,
  repo_url    TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_projects_team ON projects(team_id);

CREATE TABLE project_members (
  project_id    UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  user_id       UUID NOT NULL REFERENCES users(id),
  role          TEXT NOT NULL CHECK (role IN ('owner','member')),
  joined_at     TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (project_id, user_id)
);
```

### 3.1 v0.4.2 不变量

- **v0.4.2 改** Org owner 唯一事实源：`organization_members.role='owner'`，**不再**依赖 `organizations.owner_id`
- Org owner / Team owner / Project owner 互相独立（不蕴含）
- 删除 Project 级联清理 `channels`（E3）/ `memory_items`（E5）/ `work_items`（E8）/ `agent_project_membership`（E2）
- Email 唯一，软删不复活
- **v0.4.2 新增** Org 至少 1 个 owner（DB trigger / deferrable constraint，V1 简化应用层校验）

## 4. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| POST | `/auth/register` | 注册 | 公开 |
| POST | `/auth/login` | 登录 | 公开 |
| POST | `/auth/refresh` | 刷新 | refresh |
| POST | `/auth/logout` | 吊销 | login |
| GET / PATCH | `/me` | 当前用户 | login |
| POST | `/me/password` | 改密 | login |
| POST / GET | `/orgs` | 创建 / 列表 | login |
| GET / PATCH / DELETE | `/orgs/:id` | 详情 / 修改 / 删除 | org member / owner |
| **POST / GET** | **`/orgs/:id/members`** | **v0.4 新增** Org 成员管理 | org owner |
| **DELETE** | **`/orgs/:id/members/:uid`** | **v0.4 新增** | org owner |
| POST / GET | `/teams` | 创建 / 列表 | org member |
| GET / PATCH | `/teams/:id` | 详情 / 修改 | team member |
| POST / DELETE | `/teams/:id/members` | 邀请 / 移除 | team owner |
| POST / GET | `/projects` | 创建 / 列表 | team member |
| GET / PATCH / DELETE | `/projects/:id` | 详情 / 修改 / 删除 | project member / owner |
| POST / GET / DELETE | `/projects/:id/members` | 邀请 / 列表 / 移除 | project owner |

## 5. 关键流程

### 5.1 注册 → 加入 Org → 创建 Project

```
POST /auth/register → JWT 双令牌
POST /orgs {name} → 创建 Org + organization_members(creator, role='owner')
POST /orgs/:id/teams → 创建 team + team_members
POST /projects → 创建 project + project_members(creator, role='owner')
```

### 5.2 三层 owner 解耦（v0.4 修订）

```
Org owner A, Team owner B, Project owner C 可以是不同人
- Org owner 改 Org 名 / 删除 Org / 转让所有权
- Team owner 改 Team 名 / 邀请成员
- Project owner 改 Project 设置 / 邀请成员 / 绑定 Provider
三者权限**正交**，互不蕴含
```

### 5.3 Token 刷新轮转

同 v0.3 行为（吊销旧 jti，发新 jti）。

### 5.4 登录限流

```
Redis rl:{email_or_ip}:/auth/login  滑窗 5/min
```

## 6. UI

- P1 登录 / 注册
- P8 团队与组织设置（**v0.4 新增** Org Member 管理区块）
- `<UserAvatar>` 6px 圆角（人类）
- `<TeamSelector>` / `<ProjectSwitcher>`

## 7. 验收标准

### 7.1 功能

- **F1** 注册 → 登录 → access 15min + refresh 30d
- **F2** refresh 轮转：旧 jti 立即失效
- **F3** 三层 owner 独立：同一 User 可同时是 Org owner + Team member + Project owner
- **F4** Org member 列表 API：返回所有成员 + 角色
- **F5** Project **无** provider 字段（schema 校验）
- **F6** 删除 Project 级联 channels / messages / memory / work_items
- **F7** 登录限流 5/min，6 次返 429
- **F8** email 唯一

### 7.2 E2E

- `e2e/E1-001-register-login`
- `e2e/E1-002-org-team-project-lifecycle`
- `e2e/E1-003-refresh-rotation`
- `e2e/E1-004-permission-isolation`：三层 owner 正交
- `e2e/E1-005-rate-limit`
- `e2e/E1-006-org-member-management`（v0.4 新）

### 7.3 非功能

- 注册 P95 < 300ms
- `/me` P95 < 50ms
- 1000 并发登录无 5xx（限流后）

## 8. 与其他 Epic 的关系

- **被依赖**：E2（owner_user_id）/ E3（project_id）/ E5（owner_user_id）/ E6（subject_id）/ E7（agent_id owner）/ E8（project_id）/ E10（actor_id）
- **依赖**：无（最底层）
- **冲突裁决**：与 PRD v0.4 §4 / SYSTEM_DESIGN v0.9 §3.1 auth/iam 对齐

## 9. 风险与开放问题

- **R1**：Org 多 owner 邀请的二次确认流程（M1 后可优化）
- **R2**：avatar V1 用外链 URL，V2 接 S3（与 E3 附件同链路）
- **R3**：Org → Team → Project 嵌套层级是否要支持 Team 共享多 Project？— **当前是**（一个 Team 多个 Project 是默认关系）

## 10. 实施顺序（M1）

1. `apps/api` ASP.NET Core + EF Core(Npgsql) + PG schema（v0.5 技术栈拍板）
2. `/auth/*` + JWT + Redis rt
3. `/orgs` + `/orgs/:id/members`（v0.4 新增）
4. `/teams` / `/projects`（v0.4 简化 Project schema）
5. `/me`
6. E2E 套件
7. 前端 P1 / P8
