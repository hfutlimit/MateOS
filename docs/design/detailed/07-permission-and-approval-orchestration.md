# Detailed Design · 07 · Permission and Approval Orchestration

> **v0.4.3 修正**（P1-8）：拆分 `propose_memory` vs `write_memory` 两个权限键。
> `propose_memory` 用于申请 endpoint（ALLOW 通过），`write_memory` 仅内部 service 用。
> 前置：[00-overview.md](./00-overview.md) / [04-resolver-and-routing.md](./04-resolver-and-routing.md) / [05-memory-approval-flow.md](./05-memory-approval-flow.md)

## 0. 范围

- **v0.4.3 改**：8 键权限矩阵（新增 `propose_memory`）
- checkPermission 同步纯函数（只决 ALLOW/DENY）
- policy.evaluate 业务层显式
- Redis 缓存 + pub/sub 失效
- 三层覆盖

## 1. 权限模型（v0.4.3）

### 1.1 8 键（v0.4.3 新增 propose_memory）

| 键 | 含义 | 默认（Human owner / member / Agent ACTIVE） |
| --- | --- | --- |
| `read_message` | 读消息 | ALLOW / ALLOW / ALLOW |
| `write_message` | 发消息 | ALLOW / ALLOW / ALLOW |
| **`propose_memory`** | **申请 memory proposal**（v0.4.3 新增） | **ALLOW / ALLOW / ALLOW** |
| `write_memory` | 直接写已批准 memory（v0.4.3 改：仅 service-to-service） | DENY / DENY / DENY |
| `execute_code` | 执行代码 | DENY / DENY / DENY |
| `create_pr` | 创建 PR | REQUIRE_APPROVAL / DENY / DENY |
| `approve_memory` | 批准 memory | ALLOW / DENY / DENY |
| `manage_channel` | 管理 channel | ALLOW / DENY / DENY |

### 1.2 v0.4.3 关键拆分原因

| 端点 | 旧 Guard | v0.4.3 修复 |
| --- | --- | --- |
| `POST /memory-proposals` | `write_memory` → REQUIRE_APPROVAL → Guard 拒绝 403（**bug**） | `propose_memory` → ALLOW → 通过 |
| `POST /memory-items/direct` (内部) | `write_memory` | `write_memory` → 仅 service token |
| `POST /memory-proposals/:id/approve` | `approve_memory` | `approve_memory`（不变） |

**关键**：`/memory-proposals` 端点**不**挂只决 ALLOW 的 Guard 拒绝——业务上"申请"本来就是 ALLOW。

### 1.3 三态

```ts
type PermEffect = 'ALLOW' | 'DENY' | 'REQUIRE_APPROVAL';
```

**关键修正（v0.4.2）**：`REQUEST` → `REQUIRE_APPROVAL`（避免与 CollaborationRequest / HTTP Request 概念冲突）

## 2. checkPermission 同步纯函数

（同 v0.4.2，接口不变）

```ts
function checkPermission(
  subject: MemberRef,
  perm: PermKey,
  scope: { type: 'PROJECT' | 'CHANNEL', id: string }
): Promise<PermEffect>;
```

## 3. Guard 拆分（v0.4.2 不变）

### 3.1 旧设计（v0.4.1 错的）

```ts
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('write_memory')  // Guard 收到 REQUIRE_APPROVAL 静默放行
async proposeMemory() { /* 写 memory，绕过审批 */ }
```

### 3.2 新设计（v0.4.2/v0.4.3）

```ts
// /memory-proposals 端点：用 propose_memory（ALLOW 通过）
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('propose_memory')  // 默认 ALLOW
@Post('/memory-proposals')
async proposeMemory(@Body() body, @Req() req) {
  // Guard 已通过
  const proposal = await db.insert_memory_proposal({...body, status: 'PROPOSED'});
  return proposal;
}

// /memory-items/:id/approve：用 approve_memory
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('approve_memory')  // 仅 project owner
@Post('/memory-proposals/:id/approve')
async approveMemory(@Param('id') id) { ... }

// 内部 service 调 write_memory：service token
@UseGuards(InternalTokenGuard)
@Post('/memory-items/internal')
async writeMemoryInternal() { ... }
```

### 3.3 Guard 实现

```ts
@Injectable()
class PermissionGuard implements CanActivate {
  async canActivate(ctx: ExecutionContext): Promise<boolean> {
    const req = ctx.switchToHttp().getRequest();
    const perm = this.reflector.get('permission', ctx.getHandler());
    const scope = this.resolveScope(req);

    const effect = await checkPermission(req.user, perm, scope);
    if (effect === 'ALLOW') return true;

    // DENY 或 REQUIRE_APPROVAL → 一律 403
    throw new ForbiddenException(`Permission ${perm} requires approval but guard denied`);
  }
}
```

**v0.4.3 关键**：
- Guard 只决 ALLOW/DENY
- REQUIRE_APPROVAL 视为 403
- 业务层想走审批必须**显式** policy.evaluate

## 4. policy.evaluate

### 4.1 接口

```ts
interface PolicyContext {
  actor: MemberRef;
  project_id: UUID;
  channel_id?: UUID;
  body: any;
}

interface Policy {
  evaluate(perm: PermKey, context: PolicyContext): Promise<PermEffect>;
}
```

### 4.2 v0.4.3 REQUIRE_APPROVAL 路由

| perm | 路由 | 落实 epic |
| --- | --- | --- |
| `propose_memory` | **v0.4.3 改**：不路由——ALLOW 直接走 endpoint | E5 |
| `write_memory` | **v0.4.3 改**：仅 internal service（不通过 Guard） | E5 |
| `create_pr` | E4 调 `collaboration_requests`（V3+ 启用） | E4 |
| 其他 | 默认 DENY | — |

**V1 简化**：仅 `create_pr` 真正走门禁；V1 时 `create_pr` 默认 DENY。

## 5. Redis 缓存

（同 v0.4.2）

```
perm:{scope_type}:{scope_id}:{subject_type}:{subject_id}
  → HASH { perm_key: effect }
  → TTL 300s
```

## 6. 数据模型

```sql
CREATE TABLE permissions (
  id           UUID PRIMARY KEY,
  scope_type   TEXT NOT NULL CHECK (scope_type IN ('PROJECT','CHANNEL')),
  scope_id     UUID NOT NULL,
  subject_type TEXT NOT NULL CHECK (subject_type IN ('USER','AGENT')),
  subject_id   UUID NOT NULL,
  perm_key     TEXT NOT NULL CHECK (perm_key IN (
                'read_message','write_message','propose_memory','write_memory',
                'execute_code','create_pr','approve_memory','manage_channel')),
  effect       TEXT NOT NULL CHECK (effect IN ('ALLOW','DENY','REQUIRE_APPROVAL')),
  created_at   TIMESTAMPTZ DEFAULT now(),
  UNIQUE (scope_type, scope_id, subject_type, subject_id, perm_key)
);
```

## 7. 端点

```http
GET    /projects/:id/permissions
POST   /projects/:id/permissions  Body: { subject_type, subject_id, perm_key, effect }
DELETE /projects/:id/permissions/:pid

GET    /channels/:id/permissions
POST   /channels/:id/permissions
DELETE /channels/:id/permissions/:pid
```

## 8. 默认矩阵（v0.4.3 改）

```python
DEFAULT_MATRIX = {
    ('USER', 'owner'): {
        'read_message': 'ALLOW',
        'write_message': 'ALLOW',
        'propose_memory': 'ALLOW',      # v0.4.3 新增
        'write_memory': 'DENY',         # v0.4.3 改：仅 service
        'execute_code': 'DENY',
        'create_pr': 'REQUIRE_APPROVAL',
        'approve_memory': 'ALLOW',
        'manage_channel': 'ALLOW',
    },
    ('USER', 'member'): {
        'read_message': 'ALLOW',
        'write_message': 'ALLOW',
        'propose_memory': 'ALLOW',      # v0.4.3 新增
        'write_memory': 'DENY',
        'execute_code': 'DENY',
        'create_pr': 'DENY',
        'approve_memory': 'DENY',
        'manage_channel': 'DENY',
    },
    ('AGENT', 'lifecycle=ACTIVE'): {
        'read_message': 'ALLOW',
        'write_message': 'ALLOW',
        'propose_memory': 'ALLOW',      # v0.4.3 新增
        'write_memory': 'DENY',
        'execute_code': 'DENY',
        'create_pr': 'DENY',
        'approve_memory': 'DENY',
        'manage_channel': 'DENY',
    },
}
```

## 9. 端点 Guard 配置总览（v0.4.3）

| 端点 | Guard permission | 默认效果 |
| --- | --- | --- |
| `POST /channels/:id/messages` | `write_message` | ALLOW |
| `GET /channels/:id/messages` | `read_message` | ALLOW |
| **`POST /memory-proposals`** | **`propose_memory`** | **ALLOW（v0.4.3 改）** |
| `POST /memory-proposals/:id/approve` | `approve_memory` | ALLOW（仅 project owner） |
| `POST /memory-proposals/:id/reject` | `approve_memory` | ALLOW（仅 project owner） |
| `POST /memory-items/direct`（内部） | `write_memory` | DENY（外部），service token 通过 |
| `POST /collab-requests/auto-create-pr` | `create_pr` | REQUIRE_APPROVAL（V3+） |

## 10. v0.4.2 修复的安全 Bug（v0.4.3 强化）

**v0.4.1 漏洞**：

```ts
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('write_memory')  // Guard 收到 REQUIRE_APPROVAL 静默放行
async proposeMemory() { /* 写 memory，绕过审批 */ }
```

**v0.4.2 修复**：

```ts
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('write_memory')  // Guard 收到 REQUIRE_APPROVAL → 403
async proposeMemory() { /* 不会到这 */ }

// 业务层想用 REQUIRE_APPROVAL 必须显式 policy.evaluate
```

**v0.4.3 强化**：

```ts
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('propose_memory')  // ALLOW 通过
@Post('/memory-proposals')
async proposeMemory() {
  // 业务层不需要再调 policy.evaluate
  // 审批在 P6 独立 endpoint 走（approve_memory）
}
```

**v0.4.3 简化**：
- `propose_memory` = ALLOW（业务直接通过）
- `write_memory` = DENY（外部端点全部走 proposal 流程）
- `approve_memory` = ALLOW（owner 端点走）

## 11. E2E 验收点

```
e2e/07-permission-orchestration/
  test_001_default_matrix.json
    Given Human owner
    When check write_message
    Then ALLOW

  test_002_propose_memory_allowed.json              # v0.4.3 新增
    Given Agent lifecycle=ACTIVE
    When POST /memory-proposals
    Then 200 (propose_memory=ALLOW through Guard)

  test_003_write_memory_denied.json                  # v0.4.3 新增
    Given any user
    When POST /memory-items/direct (without service token)
    Then 403 (write_memory=DENY)

  test_004_channel_override.json
    Given Channel has 'write_message=DENY for Agent X'
    When Agent X writes
    Then 403

  test_005_cache_invalidation.json
    Given Channel override
    When DELETE override
    Then within 1s, Agent can write (cache miss → 重建)

  test_006_multi_instance_invalidation.json
    Given 2 API instances
    When instance A updates permission
    Then instance B's cache invalidated within 1s (via pub/sub)

  test_007_guard_bypass_resistance.json
    When Guard returns REQUIRE_APPROVAL
    Then 403 (NOT silent pass-through)

  test_008_approve_memory_only_owner.json
    Given Human member
    When approve memory
    Then 403 (approve_memory=DENY for member)

  test_009_lifecycle_pause_deny.json
    Given Agent lifecycle=PAUSED
    When write message
    Then 403
```

## 12. 与其他设计的关系

- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（Resolver 调度时用 permission 过滤）
- 详见 [05-memory-approval-flow.md](./05-memory-approval-flow.md)（propose_memory + approval 流程）
- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（消息流的 permission 校验）
