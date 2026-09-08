# E2 · Agent as First-Class Member

| 字段 | 值 |
| --- | --- |
| Epic ID | E2 |
| 标题 | Agent as First-Class Member |
| 阶段 | MVP（M3） |
| 上游 | PRD v0.3 §4.2-§4.3 / §5 FR-1/FR-2/FR-3 / SYSTEM_DESIGN v0.2 §3 agent-registry |
| 下游 | E3（Channel 成员）、E4（Decision 发起方）、E7（Runtime 承载）、E9（执行后端切换） |
| 状态 | Draft |

## 1. 背景与动机

Agent 不是工具，是一等成员。这要求数据模型、UI 列表、权限系统都把 Agent 当作"跟 Human 同等地位"的对象处理。本 epic 解决：

- 谁能创建 Agent、怎么配置、谁来管
- Agent 怎么加到 Channel / Project
- 6 态状态机的语义与切换规则
- Credential 与 Agent 分离（PRD §4.3 硬性要求）

## 2. 范围

### 2.1 In Scope

- Agent CRUD（name / role / capabilities / can_execute / can_review）
- Credential CRUD（provider / secret 加密存储 / meta），一对多
- Agent 与 Credential 绑定（可热切换）
- Agent 与 Project / Channel 成员关系（E1 project_members 复用）
- 6 态状态机（OFFLINE / AVAILABLE / THINKING / WORKING / WAITING_CONTEXT / ERROR）
- ERROR 触发与恢复（SYSTEM_DESIGN §6.1）
- Agent 拥有者可暂停 / 启用 / 限额管理
- 日 / 月成本 + 限额 + 用量统计
- Owner 视角的 Agent Card（P3，UI DS §5.3）

### 2.2 Out of Scope

- Provider Adapter 多实现（V1 跑通 OpenAI-compatible HTTP 即可；Anthropic / Google 等通过同一 adapter 接入，V2 完善）
- 自动限频算法（V1 用简单 token 计数 + 日限额硬上限）
- Agent marketplace / 共享 Agent（V2+）
- Agent 自训练 / 微调（Non Goal）

## 3. 数据模型

```sql
-- credentials (凭据池，User 拥有，可被多个 Agent 复用)
CREATE TABLE credentials (
  id              UUID PRIMARY KEY,
  user_id         UUID NOT NULL REFERENCES users(id),
  provider        TEXT NOT NULL,           -- 'openai' | 'anthropic' | 'google' | ...
  label           TEXT NOT NULL,           -- 'Anthropic 工作区'
  secret_encrypted BYTEA NOT NULL,         -- AES-256-GCM 信封加密
  meta            JSONB,                   -- { "base_url": "...", "region": "..." }
  last_used_at    TIMESTAMPTZ,
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_credentials_user ON credentials(user_id);

-- agents
CREATE TABLE agents (
  id              UUID PRIMARY KEY,
  owner_user_id   UUID NOT NULL REFERENCES users(id),
  credential_id   UUID NOT NULL REFERENCES credentials(id),
  name            TEXT NOT NULL,
  role            TEXT NOT NULL,           -- 'Backend Developer' 等显示名
  -- capabilities 用 canonical key，前端 i18n 渲染
  capabilities    JSONB NOT NULL DEFAULT '[]',  -- ["coding","debugging","review"]
  can_execute     BOOLEAN NOT NULL DEFAULT false,
  can_review      BOOLEAN NOT NULL DEFAULT false,
  -- 6 态；缓存值，真源在 Redis presence
  status          TEXT NOT NULL DEFAULT 'OFFLINE'
                  CHECK (status IN ('OFFLINE','AVAILABLE','THINKING','WORKING','WAITING_CONTEXT','ERROR')),
  status_reason   TEXT,                    -- ERROR 时的 fix_hint 摘要
  -- 用量与限额
  daily_limit_usd  NUMERIC(10,2) DEFAULT 5.00,
  monthly_budget_usd NUMERIC(10,2) DEFAULT 50.00,
  -- 元信息
  created_at      TIMESTAMPTZ DEFAULT now(),
  updated_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_agents_owner ON agents(owner_user_id);
CREATE INDEX idx_agents_status ON agents(status);

-- agent_project_membership (Agent 在 Project 内的可见范围)
CREATE TABLE agent_project_membership (
  agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  project_id      UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  can_read_history BOOLEAN NOT NULL DEFAULT false,  -- 是否授权读历史消息
  joined_at       TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (agent_id, project_id)
);

-- decision_records 由 E4 定义
-- memory_items 由 E5 定义
-- audit_logs 由 E8 定义
```

### 3.1 6 态语义

| 状态 | 含义 | 进入条件 | 退出条件 |
| --- | --- | --- | --- |
| `OFFLINE` | 未部署 / owner 暂停 / 日限额触顶 | owner 启用 | Runtime 上线 |
| `AVAILABLE` | 空闲 | 心跳 + idle | 收到 mention |
| `THINKING` | 正在判断 | 收到 mention | 产出 decision |
| `WORKING` | 正在产出 | decision=ACCEPT | 产出完成 |
| `WAITING_CONTEXT` | 等人类补齐 | decision=NEED_CONTEXT | 人类 @ 一次 |
| `ERROR` | 失败 | Provider 5xx×3 / 401 / 限额 | owner 处理 |

**注**：`status` 字段是缓存值，**真源在 Redis `presence:{agent_id}`**。WS 事件 `agent.status_changed` 推前端（E7 落地）。

### 3.2 Credential 加密

- 加密：AES-256-GCM，密文 = nonce(12) || ciphertext || tag(16)
- 密钥来源：KMS 信封加密（生产）；MVP 用环境变量 `MATEOS_CRED_KEY` 32 字节 base64（**部署清单必须提醒**）
- 永不回显明文。API 返回只暴露 provider / label / meta

## 4. API

### 4.1 REST

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| POST | `/credentials` | 新建凭据 | owner |
| GET | `/credentials` | 列出我的凭据 | owner |
| DELETE | `/credentials/:id` | 删除（无 Agent 引用时） | owner |
| POST | `/agents` | 创建 Agent（绑定 credential_id） | owner |
| GET | `/agents` | 列出我拥有的 Agent | owner |
| GET | `/agents/:id` | 详情 | owner 或同 project member |
| PATCH | `/agents/:id` | 修改配置（可换 credential） | owner |
| DELETE | `/agents/:id` | 删除（先退出所有 channel） | owner |
| POST | `/agents/:id/pause` | 暂停 → OFFLINE | owner |
| POST | `/agents/:id/activate` | 启用 → AVAILABLE | owner |
| GET | `/agents/:id/usage` | 用量与决策分布 | owner |
| POST | `/projects/:id/agents/:aid` | 加入 Project | project owner + agent owner 双批准 |
| DELETE | `/projects/:id/agents/:aid` | 退出 | 同上 |
| PATCH | `/projects/:id/agents/:aid` | 设置 can_read_history | project owner |
| POST | `/agents/:id/heartbeat` | Runtime 上行心跳（E7 内部用） | agent token |

### 4.2 WebSocket（订阅事件）

- `agent.status_changed` — `payload: { agent_id, status, reason, since }`
- `agent.usage_updated` — `payload: { agent_id, daily_usd, monthly_usd }`

### 4.3 错误码

| HTTP | 含义 |
| --- | --- |
| 400 | capabilities 为非数组 / provider 不支持 |
| 403 | 操作他人 Agent / Project 权限不足 |
| 409 | 删除有 Channel 引用 / Credential 仍在使用 |
| 422 | 心跳上报 status 不在 6 态枚举 |

## 5. 关键流程

### 5.1 创建 Agent

```
1. POST /agents { name, role, capabilities, credential_id, daily_limit_usd, ... }
   → 校验 credential.user_id = 当前 user（凭据归属）
   → 写入 agents，status=OFFLINE
   → 不发 heartbeat → 仍 OFFLINE 直到 E7 Runtime 拉起
   → 写 audit_logs
2. WS 推 agent.status_changed 给 owner 当前所有打开的 tab
```

### 5.2 双批准加入 Project

```
频道主（project owner）在 P3 Agent Card 点 "加入项目"
  → 创建 invite（status=PENDING），同时 WS 推给 Agent owner
  → Agent owner 在 P3 点 "批准" / "驳回"
  → 批准 → 写 agent_project_membership，WS 推 project 内所有成员 presence.updated
  → 驳回 → invite status=REJECTED，audit 落
```

设计理由（PRD §4.3 推论 + UI DS §5.3）：Project 的"知识边界"由 Project owner 决定，但 Agent 资源（成本 / 凭据）由 Agent owner 决定——**两边都点头才放行**。

### 5.3 ERROR 触发与恢复

```
触发：
- Provider HTTP 5xx 连续 3 次 → 立即 ERROR
- Provider 401/403 → 立即 ERROR，reason='auth_failed'
- 日限额 ≥ 100% → 立即 ERROR，reason='daily_limit_reached'
- Sandbox 启动失败 → 立即 ERROR，reason='sandbox_init_failed'

恢复：
- 心跳下次成功 → 自动 → AVAILABLE
- owner POST /agents/:id/activate → 强制 → AVAILABLE
- owner 更新 credential 并 PATCH /agents/:id/credential_id → AVAILABLE
```

### 5.4 心跳保活（E7 配合）

```
WS envelope: { type: 'status', payload: { status, reason?, since } }
Runtime 推 → api 写 Redis presence:{id} = {status, since, last_heartbeat=now}, TTL 90s
TTL 过期 → Background worker 扫描 → 自动 status=OFFLINE + WS 广播
```

## 6. UI

### 6.1 页面 / 组件

- **P2 我的 Agents**（待做）：列表 + 创建向导
- **P3 Agent Card**（已有 v0.4 原型）：一等成员详情页
  - 状态与身份、当前工作、能力清单、可见范围、用量与成本、运行配置、最近活动
- 顶栏 / 上下文面板 状态点（与 P5 共用 6 态语义色，UI DS §3.2）

### 6.2 关键状态展示

| 状态 | UI 表现 |
| --- | --- |
| OFFLINE | 灰点；下拉置灰不可 @ |
| AVAILABLE | 绿点 |
| THINKING | 紫点 + 呼吸动画（1.4s） |
| WORKING | 蓝点 + 流式光标 |
| WAITING_CONTEXT | **琥珀点** + 顶栏计数 +1 + 待办卡 |
| ERROR | 红点 + 错误信息 + 「查看日志」按钮 |

### 6.3 Agent Card 能力清单

- 已启用：勾 + canonical key
- 未启用：横杠 + 灰色
- 鼠标 hover 显示 `display_name`（中文/英文跟随用户 i18n）

## 7. 验收标准

### 7.1 功能

- **F1** 创建 Agent 必填 credential（归属必须为当前 user，否则 403）
- **F2** Credential 加密落库；任何 GET 不返回明文
- **F3** 切换 credential_id 后下次心跳用新凭据调用 Provider
- **F4** 6 态之间转换符合 §5.3 规则（用单元测试枚举所有合法迁移）
- **F5** ERROR 状态不出现在 Resolver 候选（M4 验证）
- **F6** 心跳丢失 90s 后自动转 OFFLINE
- **F7** 双批准流程：project owner 与 agent owner 任一未批准前不写入 membership
- **F8** 日限额触顶后立即 ERROR；次日 0 点自动归零
- **F9** 暂停 / 启用 是 owner-only 操作
- **F10** 删除有 Channel 引用的 Agent 返 409

### 7.2 E2E

- `e2e/E2-001-create-agent`：建 Agent + 心跳 → AVAILABLE
- `e2e/E2-002-credential-rotation`：换 credential → 后续调用用新 key
- `e2e/E2-003-double-approval`：project owner 批准 + agent owner 批准 → membership 生效
- `e2e/E2-004-error-recovery`：模拟 Provider 401 → ERROR → 改 credential → 恢复 AVAILABLE
- `e2e/E2-005-daily-limit`：跑满 5.00 美元 → ERROR → 次日恢复

### 7.3 非功能

- 心跳处理 P99 < 50ms
- 状态广播 P99 < 200ms（WS 推 → 落 Redis → fanout）
- 1000 Agent × 90s 心跳：Redis presence key < 5MB

## 8. 与其他 Epic 的关系

- **被依赖**：
  - E3 Channel 成员邀请需要 agent 存在
  - E4 Decision 发起方是 agent_id
  - E7 Runtime 启动后向 /agents/:id/heartbeat 上报
  - E9 执行后端切换时 agent 必须可降级为「通过 AgentBoard dispatch」
- **依赖**：E1（提供 owner_user_id / project）
- **冲突裁决**：6 态枚举对齐 SYSTEM_DESIGN §6.1 / UI DS §4

## 9. 风险与开放问题

- **R1**：日限额归零时区（用户 TZ vs UTC）→ **MVP 用 UTC 0 点，与 cron 对齐**
- **R2**：Capacities canonical key 枚举是 hardcoded 还是 DB 化？→ **MVP hardcode 在 `packages/contracts`（`coding/debugging/review/testing/architecture`），V2 提升为 DB 表**
- **R3**：ERROR 触发的"连续 3 次 5xx"窗口期（5min 滑窗）→ **MVP 简单计数，重启归零**
- **R4**：Agent owner 撤销 Project 邀请时，project owner 已批准怎么办？→ **只要任一未批准就不生效，两边都批准后单边撤销即可（写 audit）**

## 10. 实施顺序（M3 同步 E7 启动）

1. credentials 表 + AES-256-GCM 工具 + REST
2. agents 表 + REST（不含 6 态广播）
3. agent_project_membership + 双批准流程
4. WS `agent.status_changed` 事件 + Redis presence 写入
5. 心跳端点 + 90s 过期扫描
6. P2 / P3 UI（前端）
7. E2E 套件
