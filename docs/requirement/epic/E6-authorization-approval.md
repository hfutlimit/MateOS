# E6 · Authorization & Approval

| 字段 | 值 |
| --- | --- |
| Epic ID | E6 |
| 标题 | Authorization & Approval |
| 阶段 | MVP（M4 同步） |
| 上游 | PRD v0.4 §5 FR-9 / §6 / SYSTEM_DESIGN v0.3 §7 / UI DS v0.5 |
| 下游 | E1（subject）/ E2（can_execute/can_review 已删除；仅作为受控对象）/ E3（channel scope）/ E4（permission 决策依据）/ E5（write_memory = REQUIRE_APPROVAL）/ E7（execute_code / create_pr） |
| 状态 | Draft（v0.4 修订版） |

## 1. 背景与动机

Permission Model 是 MateOS 安全核心。v0.4 + v0.4.2 关键变化：
1. **删除** Agent 的 `can_execute` / `can_review` 字段——Capability（能不能，E2）+ Permission（允不允许，本 epic）单一事实源
2. **effect 改三态**：`ALLOW` / `DENY` / **`REQUIRE_APPROVAL`**（替换 `REQUEST`，避免与 CollaborationRequest / HTTP Request 概念冲突）
3. **v0.4.2 改** **`REQUIRE_APPROVAL` 不再让普通 Guard 静默放行**——Guard 拆为 `checkPermission`（只决 ALLOW/DENY）+ `policy.evaluate()`（业务层显式调用），避免"Guard 放行后忘记审批"的安全 bug

## 2. 范围

### 2.1 In Scope

- 7 个权限键
- 默认权限矩阵（v0.4 改：write_memory / create_pr = REQUIRE_APPROVAL）
- Project 级 / Channel 级覆盖
- `check(subject, perm, scope) → ALLOW | DENY | REQUIRE_APPROVAL`（v0.4 改三态）
- **v0.4.2 改** `policy.evaluate()` 业务层显式调用（不带"放行"含义）
- 三层合并：Channel > Project > 默认
- Redis 缓存 + `perm.changed` pub/sub 失效
- NestJS Guard `@RequirePermission('perm_key')`：**只决 ALLOW/DENY**，REQUIRE_APPROVAL 直接拒绝（默认 deny-by-default）
- 业务层显式走审批路径：
  - `write_memory` → E5 调 `memory_proposals` 创建 + P6 审批
  - `create_pr` → E4 调 `collaboration_requests` 创建（V3+ 启用）

### 2.2 Out of Scope

- RBAC 角色继承（V2）
- 临时权限 / 邀请链接（V2+）
- 资源级权限（Personal Memory owner / WorkItem creator）—— 单独字段控制

## 3. 数据模型

```sql
-- v0.4 改：effect 三态
CREATE TABLE permissions (
  id           UUID PRIMARY KEY,
  scope_type   TEXT NOT NULL CHECK (scope_type IN ('PROJECT','CHANNEL')),
  scope_id     UUID NOT NULL,
  subject_type TEXT NOT NULL CHECK (subject_type IN ('USER','AGENT')),
  subject_id   UUID NOT NULL,
  perm_key     TEXT NOT NULL CHECK (perm_key IN (
                'read_message','write_message','write_memory',
                'execute_code','create_pr','approve_memory','manage_channel')),
  effect       TEXT NOT NULL CHECK (effect IN ('ALLOW','DENY','REQUIRE_APPROVAL')),  -- v0.4 改
  created_at   TIMESTAMPTZ DEFAULT now(),
  UNIQUE (scope_type, scope_id, subject_type, subject_id, perm_key)
);
CREATE INDEX idx_perm_scope ON permissions(scope_type, scope_id);
CREATE INDEX idx_perm_subject ON permissions(subject_type, subject_id);

-- perm:{scope_type}:{scope_id}:{subject_type}:{subject_id}（Redis Hash）
--   field=perm_key, value=ALLOW|DENY|REQUIRE_APPROVAL
--   TTL 5min，事件 perm.changed 失效
```

### 3.1 默认权限矩阵（v0.4 改）

| 权限 | Human owner | Human member | Agent (lifecycle=ACTIVE) |
| --- | --- | --- | --- |
| `read_message` | ALLOW | ALLOW | ALLOW（已加入 channel） |
| `write_message` | ALLOW | ALLOW | ALLOW |
| `write_memory` | **REQUIRE_APPROVAL** | **REQUIRE_APPROVAL** | **REQUIRE_APPROVAL** |
| `execute_code` | DENY | DENY | DENY（V1） |
| `create_pr` | **REQUIRE_APPROVAL**（V3+） | DENY | DENY |
| `approve_memory` | ALLOW | DENY | DENY |
| `manage_channel` | ALLOW | DENY | DENY |

### 3.2 `REQUIRE_APPROVAL` 路由（v0.4 明确）

| 权限键 | 路由 | 落实 epic |
| --- | --- | --- |
| `write_memory` | E5 memory_proposals + 审批 | E5 |
| `create_pr` | E4 CollaborationRequest（V3+ 启用） | E4 / V3 |
| 其他 | 默认 DENY | — |

> **关键**：Permission 层**不实现**审批逻辑，只判 effect。审批路径由业务层（E5 / E4）实现。

## 4. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET / POST / DELETE | `/projects/:id/permissions` | Project 覆盖 | project owner |
| GET / POST | `/channels/:id/permissions` | Channel 覆盖 | channel owner |
| POST | `/check`（内部） | sync check | service token |

### 4.2 WebSocket

- 无（sync check 是 service-to-service）

### 4.3 内部 API

```ts
// packages/contracts（v0.4 改）
type PermKey = 'read_message' | 'write_message' | 'write_memory' | 'execute_code' | 'create_pr' | 'approve_memory' | 'manage_channel';
type PermEffect = 'ALLOW' | 'DENY' | 'REQUIRE_APPROVAL';  // v0.4 改三态

function check(
  subject: MemberRef,
  perm: PermKey,
  scope: { type: 'PROJECT' | 'CHANNEL', id: string }
): PermEffect;
```

## 5. 关键流程

### 5.1 判定流程（不变）

```
check(subject, perm, scope):
  1. Redis 缓存命中 → 解析
  2. Miss → 重建（Channel → Project → 默认）
  3. 写缓存
  4. 返回 effect
```

### 5.2 Guard 拦截（v0.4.2 改：拆为 check + policy）

```ts
// packages/contracts

// 只决 ALLOW / DENY，REQUIRE_APPROVAL 视为 DENY
// Guard 不再"放行 + 走门禁"——避免安全 bug
function checkPermission(
  subject: MemberRef,
  perm: PermKey,
  scope: { type: 'PROJECT' | 'CHANNEL', id: string }
): 'ALLOW' | 'DENY' | 'REQUIRE_APPROVAL';  // 仍返回三态，但 Guard 只放 ALLOW

// 业务层显式调用，REQUIRE_APPROVAL 必须走业务门禁
function policy(): {
  evaluate(perm: PermKey, context: PolicyContext): Promise<PolicyDecision>;
};
```

**v0.4.2 改** Guard 流程：
1. JwtGuard 解析 → req.user
2. PermissionGuard → `checkPermission(req.user, perm, scope)`
3. `ALLOW` → next
4. **`DENY` 或 `REQUIRE_APPROVAL` → 403 拒绝**（不再静默放行）
5. 业务层如需触发审批，显式：
   ```ts
   // E5 写记忆
   const decision = await policy.evaluate('write_memory', { projectId, ... });
   if (decision === 'REQUIRE_APPROVAL') {
     // 显式创建 memory_proposals + 走 E5 P6 审批
   }
   ```

**为什么这样改**（v0.4.2 关键）：
- 旧 v0.4.1：Guard 收到 REQUIRE_APPROVAL → 放行 → 业务层"忘记"调用门禁 → 审批被绕过
- 新 v0.4.2：Guard 收到 REQUIRE_APPROVAL → 拒绝；业务层必须**显式**调用 policy.evaluate() 走门禁
- 安全：调用栈里"忘了审批"就 403，强制每个 REQUIRE_APPROVAL 路径有显式调用
- 同理 E4 的 `create_pr`（V3+ 启用）、E5 的 `write_memory`（MVP）

### 5.3 缓存失效

同 v0.3。

## 6. UI

- 暂不单独做（V1 复用 Project Settings 一节）
- Agent 不可见 manage_channel / approve_memory 按钮
- `REQUIRE_APPROVAL` 触发时：业务层（E5 / E4）打开对应审批中心

## 7. 验收标准

### 7.1 功能

- **F1** 默认矩阵：Human owner 写消息 ALLOW；Agent 写消息 ALLOW（已加入 channel）
- **F2** Channel 覆盖优先
- **F3** Project 覆盖被 Channel 覆盖优先
- **F4** **v0.4.2 改** Guard 收到 REQUIRE_APPROVAL → 拒绝 403（不静默放行）
- **F5** **v0.4.2 改** 业务层显式 `policy.evaluate()` 走 E5 / E4 门禁
- **F6** **v0.4 新增** Agent 表无 can_execute / can_review 字段
- **F7** 跨实例缓存失效（pub/sub）
- **F8** approve_memory 仅 project owner

### 7.2 E2E

- `e2e/E6-001-default-matrix`
- `e2e/E6-002-channel-override`
- `e2e/E6-003-multi-instance-cache-invalidation`
- `e2e/E6-004-write-memory-gate`（v0.4.2 改：Guard 拒绝 + 业务层显式调 policy.evaluate）
- `e2e/E6-005-approval-only-owner`
- `e2e/E6-006-capability-permission-orthogonal`
- `e2e/E6-007-guard-bypass-resistance`（v0.4.2 新）—— Guard 不会静默放行 REQUIRE_APPROVAL

### 7.3 非功能

- `check()` P99 < 5ms
- 缓存命中率 > 95%

## 8. 与其他 Epic 的关系

- **被依赖**：E1/E2/E3/E4/E5/E7/E8 全部 Guard 调用
- **依赖**：E1（subject + scope）
- **冲突裁决**：三层合并 Channel > Project > 默认（SD v0.3 §7）

## 9. 风险与开放问题

- **R1**：Redis pub/sub 跨可用区有秒级延迟 → 5min TTL 兜底
- **R2**：Permission effect 三态命名（`REQUIRE_APPROVAL` vs `ALLOW_WITH_APPROVAL`）—— 已选 `REQUIRE_APPROVAL`，理由：动词清晰、与 Policy 概念对齐
- **R3**：资源级权限（Personal Memory 仅 owner）通过 owner_user_id 字段单独控制，不走 perm 表

## 10. 实施顺序（M4 同步 E4）

1. 默认矩阵 + `check()` 纯函数（packages/contracts）
2. Redis 缓存层 + Lua 原子性
3. permissions 表（v0.4 effect 三态）
4. pub/sub 失效广播
5. Guard 改造：`REQUIRE_APPROVAL` 不再返 403，走业务门禁
6. E5 接入 write_memory
7. E2E 套件
