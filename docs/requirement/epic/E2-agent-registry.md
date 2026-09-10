# E2 · Agent Registry

| 字段 | 值 |
| --- | --- |
| Epic ID | E2 |
| 标题 | Agent Registry |
| 阶段 | S1 前置（能力域 M3a/M3b） |
| 上游 | PRD v0.4 §4.2-§4.4 / §5 FR-1/FR-2/FR-3 / SYSTEM_DESIGN v0.9 §3 agent-registry / §6 Runtime |
| 下游 | E4（lifecycle+activity 用于 Resolver 过滤）、E7（agent 实体）、E8（assignee） |
| 状态 | Draft（v0.9 同步：补 max_concurrency / health） |

## 1. 背景与动机

Agent 是 MateOS 的一等成员。v0.4.2 重构后，E2 **只负责"Agent 是谁、能做什么、最大并发配置"**——**不**管运行时、**不**管执行、**不**管权限事实、**不**管调度（activity 和 Resolver 规则归 E4）。

**v0.4.2 关键收口**：
- E2 不再含 Resolver 规则（上一版还写"lifecycle × activity 过滤"——已废）
- E2 不再有 `active_slots` 字段（归 Redis）
- activity 字段仅做 UI derived（E2 仍写，因为 UI 需要显示）

## 2. 范围

### 2.1 In Scope

- Agent CRUD（name / role / capabilities / runtime / workspace，**无** can_execute/can_review 字段）
- Credential CRUD（Provider / secret 加密）
- Agent 与 Credential 绑定（可热切换）
- **v0.4 拆维度**：lifecycle（ACTIVE/PAUSED/DISABLED）+ activity（OFFLINE/AVAILABLE/THINKING/WORKING/WAITING_CONTEXT/ERROR）
- **v0.4.2 改** E2 提供 `max_concurrency`（durable config）；active slot 状态归 Redis（E4 调度）
- **v0.4 单一事实源**：Capability（能不能做，canonical key 集合）；Permission（允不允许，E6 统一管）
- Agent lifecycle 由 owner 显式控制
- Agent activity 由 Runtime / Orchestrator 自动更新（v0.4.2 起仅做 UI derived）
- Agent 在 Project 内的可见范围
- Agent token 签发 + 撤销

### 2.2 Out of Scope

- **执行 / dispatch**——归 E7
- **状态细节产生**（activity 写入路径）——Runtime (E7) 和 Orchestrator (E4) 通过 WS / API 触达
- **调度规则**（active_slots 原子 reservation、Resolver 过滤）——归 E4
- 自动限频 / 智能体 marketplace / 共享 Agent（V2+）

## 3. 数据模型

```sql
-- credentials
CREATE TABLE credentials (
  id              UUID PRIMARY KEY,
  user_id         UUID NOT NULL REFERENCES users(id),
  provider        TEXT NOT NULL,            -- 'openai' | 'anthropic' | 'google' | ...
  label           TEXT NOT NULL,
  secret_encrypted BYTEA NOT NULL,          -- AES-256-GCM 信封
  meta            JSONB,
  last_used_at    TIMESTAMPTZ,
  created_at      TIMESTAMPTZ DEFAULT now()
);

-- agents（v0.4.2 改：删 active_slots 字段——归 Redis 持有；保留 max_concurrency 配置）
CREATE TABLE agents (
  id              UUID PRIMARY KEY,
  owner_user_id   UUID NOT NULL REFERENCES users(id),
  credential_id   UUID NOT NULL REFERENCES credentials(id),
  name            TEXT NOT NULL,
  role            TEXT NOT NULL,
  capabilities    JSONB NOT NULL DEFAULT '[]',     -- canonical key 集合 ["coding","review","debugging"]
  -- 单一事实源：lifecycle 由 owner 控制，activity 仅做 UI derived（不再用于 Resolver）
  lifecycle       TEXT NOT NULL DEFAULT 'ACTIVE'
                  CHECK (lifecycle IN ('ACTIVE','PAUSED','DISABLED')),
  activity        TEXT NOT NULL DEFAULT 'OFFLINE'
                  CHECK (activity IN ('OFFLINE','AVAILABLE','THINKING','WORKING','WAITING_CONTEXT','ERROR')),
  activity_reason TEXT,                            -- ERROR 时携带 fix_hint
  -- v0.4.2 改：只存 durable config；active slot 数归 Redis
  max_concurrency INT NOT NULL DEFAULT 1,
  daily_limit_usd  NUMERIC(10,2) DEFAULT 5.00,
  monthly_budget_usd NUMERIC(10,2) DEFAULT 50.00,
  created_at      TIMESTAMPTZ DEFAULT now(),
  updated_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_agents_owner ON agents(owner_user_id);
CREATE INDEX idx_agents_lifecycle_activity ON agents(lifecycle, activity);
-- 删：active_slots 字段（v0.4.2）

CREATE TABLE agent_project_membership (
  agent_id        UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  project_id      UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  can_read_history BOOLEAN NOT NULL DEFAULT false,
  joined_at       TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (agent_id, project_id)
);

CREATE TABLE agent_tokens (
  id          UUID PRIMARY KEY,
  agent_id    UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  token_hash  TEXT NOT NULL,                  -- 哈希存储，明文只下发一次
  label       TEXT,
  last_seen_at TIMESTAMPTZ,
  expires_at  TIMESTAMPTZ,
  revoked     BOOLEAN NOT NULL DEFAULT false,
  created_at  TIMESTAMPTZ DEFAULT now()
);
```

### 3.1 Lifecycle × Activity 语义（v0.4 拆开）

| 维度 | 状态 | 含义 | UI |
| --- | --- | --- | --- |
| **lifecycle** | `ACTIVE` | owner 启用 | 正常状态点（由 activity 决定颜色） |
| | `PAUSED` | owner 主动暂停 | 灰点 + 「已暂停」徽标 |
| | `DISABLED` | 系统禁用（违规 / 滥用） | 灰点 + 「已禁用」徽标 |
| **activity** | `OFFLINE` | 心跳丢失 > 90s | 灰点 |
| | `AVAILABLE` | 空闲且 lifecycle=ACTIVE | 绿点 |
| | `THINKING` | 已收到 CollaborationRequest，正在判断 | 紫点 + 呼吸 |
| | `WORKING` | 已 ACCEPT，正在产生 Execution | 蓝点 + 光标 |
| | `WAITING_CONTEXT` | 决策 NEED_CONTEXT | 琥珀 + 计数 |
| | `ERROR` | Provider 失败 / 限额 / 凭据失效 | 红点 + fix_hint |

**冲突规则**：lifecycle ≠ ACTIVE → 强制显示 lifecycle 徽标（activity 灰点）；lifecycle=ACTIVE → 按 activity 语义显示。

> **v0.4.2 改** Resolver 过滤（E4 维护）：`lifecycle=ACTIVE ∩ Redis tryAcquireSlot 成功`。E2 不再持有 Resolver 规则。activity 字段仅做 UI derived，**不**用于调度。

### 3.2 Capability × Permission 单一事实源（v0.4 拆开）

- **Capability**（Agent 表）：能不能做。例 `["coding", "debugging", "review", "testing", "architecture"]`
- **Permission**（E6 表）：允不允许做。例 `execute_code` / `create_pr` / `approve_memory`
- 删除 v0.3 的 `agent.can_execute` / `can_review` 字段
- v0.4 收敛为 5 个 canonical key：`coding` / `debugging` / `review` / `testing` / `architecture`

### 3.3 Credential 加密

- AES-256-GCM，密文 = nonce(12) || ciphertext || tag(16)
- 密钥：MVP 环境变量；生产 KMS 信封
- API 返回**永不**含明文

## 4. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| POST / GET | `/credentials` | 凭据 CRUD | owner |
| POST / GET | `/agents` | Agent CRUD | owner |
| GET / PATCH / DELETE | `/agents/:id` | 详情 / 修改（lifecycle / 切换 credential） | owner |
| POST | `/agents/:id/pause` | lifecycle=PAUSED | owner |
| POST | `/agents/:id/activate` | lifecycle=ACTIVE | owner |
| POST | `/agents/:id/disable` | lifecycle=DISABLED | owner（系统也可调） |
| GET | `/agents/:id/usage` | 用量 | owner |
| POST / DELETE | `/projects/:id/agents/:aid` | 加入 / 退出 Project | 双批准 |
| POST | `/agents/:id/tokens` | 签发新 token | owner |
| DELETE | `/agents/:id/tokens/:tid` | 撤销 token | owner |
| POST | `/agents/:id/activity` | **v0.4 新增** Runtime 上报 activity | agent token |

### 4.2 WS 事件

- `agent.lifecycle_changed`（v0.4 新增，独立于 activity）
- `agent.activity_changed`
- `agent.usage_updated`

## 5. 关键流程

### 5.1 创建 Agent

```
POST /agents { name, role, capabilities, credential_id }
  → 校验 credential.user_id = current user
  → 写入 agents（lifecycle=ACTIVE, activity=OFFLINE，**无** can_execute 字段）
  → 不触发 heartbeat（activity 保持 OFFLINE 直到 E7 Runtime 拉起）
```

### 5.2 双批准加入 Project（不变）

```
project owner 在 P3 点 "加入项目"
  → invite (PENDING)
  → WS 推 agent owner
  → agent owner 批准 / 驳回
  → 批准 → 写 agent_project_membership
```

### 5.3 Lifecycle 变更（v0.4 显式）

```
POST /agents/:id/pause
  → lifecycle=PAUSED
  → activity 自动转 OFFLINE（标记："lifecycle_paused"）
  → WS 推 agent.lifecycle_changed
  → E4 Resolver 自动排除
  → 不会推 E7 dispatch
```

### 5.4 Activity 更新路径（v0.4 拆开）

```
来源 1：E7 Runtime 心跳 + 任务下发
  → heartbeat: activity=AVAILABLE
  → dispatch: activity=WORKING
  → result: activity=AVAILABLE

来源 2：E4 Orchestrator
  → 收到 CollaborationRequest 派发：activity=THINKING
  → 决策产出：activity=AVAILABLE / WAITING_CONTEXT

来源 3：E7 ERROR
  → Provider 5xx×3 / 401 / 限额 → activity=ERROR

POST /agents/:id/activity (agent token)
  → 校验 lifecycle=ACTIVE（DISABLED/PAUSED 不允许转其他 activity）
  → 写 Redis presence + DB agents.activity
```

### 5.5 ERROR 触发

同 v0.3 逻辑；lifecycle 不变（仍是 ACTIVE），只是 activity 异常。

## 6. UI

### 6.1 页面

- **P3 Agent Card**（v0.4 → v0.5 回修）：新增 lifecycle 徽标 + 删 can_execute/can_review 行
- **P2 我的 Agents**：列表 + lifecycle 筛选
- 顶栏 / 上下文面板：状态点（lifecycle × activity 双维度）

### 6.2 Agent Card 状态展示

| lifecycle | activity | 圆点 | 徽标 |
| --- | --- | --- | --- |
| ACTIVE | OFFLINE | 灰 | — |
| ACTIVE | AVAILABLE | 绿 | — |
| ACTIVE | THINKING | 紫 | — |
| ACTIVE | WORKING | 蓝 | — |
| ACTIVE | WAITING_CONTEXT | 琥珀 | "等待上下文 ×N" |
| ACTIVE | ERROR | 红 | "fix_hint" |
| PAUSED | * | 灰 | "已暂停" |
| DISABLED | * | 灰 | "已禁用" |

### 6.3 P3 Agent Card 区块

| 区块 | v0.5 调整 |
| --- | --- |
| 状态与身份 | +lifecycle 徽标 + activity 单独 |
| 当前工作 | 改 `agent_executions`（最近 1 条 RUNNING 详情） |
| 能力 Capabilities | 4 个固定 key（不再"未启用"） |
| 运行配置 | **删除** can_execute / can_review 行 |

## 7. 验收标准

### 7.1 功能

- **F1** 创建 Agent 必填 credential（归属必须为当前 user）
- **F2** Credential 加密落库；任何 GET 不返回明文
- **F3** 切换 credential_id 后 Runtime 用新凭据调用
- **F4** **v0.4 新增** lifecycle 变更后立即从 Resolver 候选排除
- **F5** **v0.4 新增** activity 自动从 OFFLINE 推 AVAILABLE 心跳恢复
- **F6** 心跳 90s 过期扫描自动 activity=OFFLINE
- **F7** **v0.4 新增** Agent 表无 `can_execute` / `can_review` 字段（schema 校验）
- **F8** 双批准流程不变
- **F9** 日限额触顶立即 activity=ERROR
- **F10** 删除有 Channel 引用的 Agent 返 409
- **F11** Token 泄露可撤销 → 当前连接断开

### 7.2 E2E

- `e2e/E2-001-create-agent`
- `e2e/E2-002-credential-rotation`
- `e2e/E2-003-double-approval`
- `e2e/E2-004-error-recovery`
- `e2e/E2-005-daily-limit`
- `e2e/E2-006-lifecycle-pause-exclude-from-resolver`（v0.4 新）
- `e2e/E2-007-lifecycle-vs-activity-orthogonal`（v0.4 新）

### 7.3 非功能

- 心跳处理 P99 < 50ms
- 状态广播 P99 < 200ms
- 1000 Agent × 90s 心跳：Redis presence < 5MB

## 8. 与其他 Epic 的关系

- **被依赖**：E3（agent member）/ E4（lifecycle+activity 过滤）/ E7（agent 实体 + activity 上报）/ E8（WorkItem assignee）
- **依赖**：E1（owner_user_id / project）
- **冲突裁决**：6 态（lifecycle × activity）枚举与 SD v0.9 §6.2 / DS v0.7 §3.1 / §4 一致

## 9. 风险与开放问题

- **R1**：日限额时区（V1 用 UTC 0 点）
- **R2**：capabilities canonical key hardcode 在 `packages/contracts`，V2 升 DB 表
- **R3**：lifecycle=DISABLED 的恢复流程（V1 人工申诉，V2 自动）
- **R4**：PAUSED 期间未完成的 Execution 怎么办？→ 由 E7 在 lifecycle 变更时检查 in-flight execution，发出 cancel

## 10. 实施顺序（M3 同步 E7）

1. credentials 表 + AES-256-GCM 工具
2. agents 表（v0.4 简化 schema）
3. agent_project_membership + 双批准
4. **v0.4 新增** lifecycle 字段 + pause/activate/disable REST
5. **v0.4 新增** activity 上报 REST + WS lifecycle/activity 事件
6. 心跳端点 + 90s 过期
7. P3 / P2 UI
8. E2E 套件
