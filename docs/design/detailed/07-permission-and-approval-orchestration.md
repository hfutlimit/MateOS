# Detailed Design · 07 · Permission and Approval Orchestration

> E6 Permission 三态 + Guard 拆分 + policy.evaluate + Redis 缓存 + pub/sub 失效。
> 前置：[00-overview.md](./00-overview.md) / [04-resolver-and-routing.md](./04-resolver-and-routing.md) / [05-memory-approval-flow.md](./05-memory-approval-flow.md)

## 0. 范围

- 7 键权限矩阵
- `checkPermission` 同步纯函数（只决 ALLOW/DENY）
- `policy.evaluate` 业务层显式（REQUIRE_APPROVAL 走 E5 / E4）
- Redis 缓存 + pub/sub 失效
- 三层覆盖：Channel > Project > 默认

## 1. 权限模型

### 1.1 7 键

| 键 | 含义 | 默认（Human owner / member / Agent） |
| --- | --- | --- |
| `read_message` | 读消息 | ALLOW / ALLOW / ALLOW |
| `write_message` | 发消息 | ALLOW / ALLOW / ALLOW |
| `write_memory` | 写 memory | REQUIRE_APPROVAL / REQUIRE_APPROVAL / REQUIRE_APPROVAL |
| `execute_code` | 执行代码 | DENY / DENY / DENY |
| `create_pr` | 创建 PR | REQUIRE_APPROVAL / DENY / DENY |
| `approve_memory` | 批准 memory | ALLOW / DENY / DENY |
| `manage_channel` | 管理 channel | ALLOW / DENY / DENY |

### 1.2 三态

```ts
type PermEffect = 'ALLOW' | 'DENY' | 'REQUIRE_APPROVAL';
```

**关键修正（v0.4.2）**：
- `REQUEST` → `REQUIRE_APPROVAL`（避免与 CollaborationRequest / HTTP Request 概念冲突）

## 2. checkPermission 同步纯函数

### 2.1 接口

```ts
// packages/contracts
function checkPermission(
  subject: MemberRef,
  perm: PermKey,
  scope: { type: 'PROJECT' | 'CHANNEL', id: string }
): Promise<PermEffect>;
```

### 2.2 内部实现

```python
async def check_permission(subject, perm, scope) -> PermEffect:
    # 1. 查 Redis 缓存
    cache_key = f"perm:{scope.type}:{scope.id}:{subject.type}:{subject.id}"
    cached = await redis.hgetall(cache_key)
    if cached and perm in cached:
        return PermEffect(cached[perm])

    # 2. 缓存 miss → 重建
    effect = await _evaluate(subject, perm, scope)

    # 3. 写缓存（TTL 5 分钟）
    await redis.hset(cache_key, perm, effect.value, ex=300)
    return effect

async def _evaluate(subject, perm, scope) -> PermEffect:
    # 1. 查 Channel 级覆盖
    if scope.type == 'CHANNEL':
        effect = await db.get_effect('CHANNEL', scope.id, subject, perm)
        if effect:
            return effect

    # 2. 查 Project 级覆盖
    project_id = scope.id if scope.type == 'PROJECT' else get_project_id(scope.id)
    effect = await db.get_effect('PROJECT', project_id, subject, perm)
    if effect:
        return effect

    # 3. 默认矩阵
    return DEFAULT_MATRIX[(subject.type, perm)]
```

### 2.3 三层合并顺序

```
Channel 级 > Project 级 > 默认矩阵

如果 Channel 返 ALLOW，Project 返 DENY → 最终 ALLOW（Channel 优先）
如果 Channel 返 DENY，Project 返 ALLOW → 最终 DENY（Channel 优先）
如果 Project 没覆盖 → 默认
```

## 3. Guard 拆分（v0.4.2 关键修复）

### 3.1 旧设计（v0.4.1 错的）

```ts
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('write_memory')
@Post('/memory-proposals')
async proposeMemory(...) {
  // ⚠️ 漏洞：Guard 收到 REQUIRE_APPROVAL 后静默放行
  // 如果业务层忘记调 policy.evaluate → 审批被绕过
  await db.insert(memory);
}
```

### 3.2 新设计（v0.4.2 修复）

```ts
// Guard 只决 ALLOW / DENY
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('write_memory')  // 期待 ALLOW，否则 403
@Post('/memory-proposals')
async proposeMemory(@Body() body, @Req() req) {
  // Guard 已确保 write_memory = ALLOW
  // 业务层不需要再调 policy.evaluate
  const proposal = await db.insert_memory_proposal(body, req.user);
  return proposal;
}
```

```ts
// 业务层想触发审批，必须显式调 policy.evaluate
@Post('/memory-proposals/auto-from-agent')
async autoProposeFromAgent(@Body() body, @Req() req) {
  // 1. 业务层先 check（不是 Guard，是 policy）
  const decision = await policy.evaluate('write_memory', {
    actor: req.user,
    project_id: body.project_id,
    body
  });

  if (decision === 'DENY') {
    throw new ForbiddenException();
  }

  if (decision === 'REQUIRE_APPROVAL') {
    // 显式走 E5 proposal 流程
    const proposal = await db.insert_memory_proposal({...body, status: 'PROPOSED'});
    return { status: 'PROPOSED', message: '需要人工批准' };
  }

  // ALLOW：直接写（V1 简化，V2 移除此路径）
  return await db.insert_memory_proposal({...body, status: 'APPROVED'});
}
```

### 3.3 Guard 实现（NestJS）

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
    // 业务层想用 REQUIRE_APPROVAL 必须显式调 policy.evaluate
    throw new ForbiddenException(`Permission ${perm} requires approval but guard denied`);
  }
}
```

**关键**：Guard 永远只返回 `ALLOW` 或拒绝；`REQUIRE_APPROVAL` 视为拒绝，业务层必须**显式**处理。

## 4. policy.evaluate（业务层显式）

### 4.1 接口

```ts
// packages/contracts
interface PolicyContext {
  actor: MemberRef;                  // 谁
  project_id: UUID;
  channel_id?: UUID;
  body: any;                         // 业务请求体
}

interface Policy {
  evaluate(perm: PermKey, context: PolicyContext): Promise<PermEffect>;
}
```

### 4.2 默认实现

```python
class DefaultPolicy(Policy):
    async evaluate(perm, context) -> PermEffect:
        return await checkPermission(
            context.actor, perm,
            {'type': 'CHANNEL', 'id': context.channel_id} if context.channel_id
            else {'type': 'PROJECT', 'id': context.project_id}
        )
```

### 4.3 REQUIRE_APPROVAL 路由表

| perm | 路由 | 落实 |
| --- | --- | --- |
| `write_memory` | E5 调 `memory_proposals` 创建 | E5 |
| `create_pr` | E4 调 `collaboration_requests` 创建（V3+ 启用） | E4 |
| 其他 | 默认 DENY | — |

**V1 简化**：仅 `write_memory` 真正走门禁；`create_pr` 留接口。

## 5. Redis 缓存

### 5.1 Key 格式

```
perm:{scope_type}:{scope_id}:{subject_type}:{subject_id}
  → HASH { perm_key: effect }
  → TTL 300s
```

### 5.2 写入覆盖

```
写 permissions 表（POST/DELETE /permissions）：
  1. DB 写
  2. Redis DEL perm:{scope_type}:{scope_id}:{subject_type}:{subject_id}
  3. Redis Pub/Sub 广播 perm.changed { scope_type, scope_id, subject_type, subject_id }
```

### 5.3 跨实例失效

```
所有 API 实例订阅 perm.changed:
  on message:
    if local LRU cache has this key:
      DEL local
```

**TTL 是兜底**：即使 pub/sub 失败，5 分钟后缓存自然过期。

## 6. 数据模型

```sql
CREATE TABLE permissions (
  id           UUID PRIMARY KEY,
  scope_type   TEXT NOT NULL CHECK (scope_type IN ('PROJECT','CHANNEL')),
  scope_id     UUID NOT NULL,
  subject_type TEXT NOT NULL CHECK (subject_type IN ('USER','AGENT')),
  subject_id   UUID NOT NULL,
  perm_key     TEXT NOT NULL CHECK (perm_key IN (
                'read_message','write_message','write_memory',
                'execute_code','create_pr','approve_memory','manage_channel')),
  effect       TEXT NOT NULL CHECK (effect IN ('ALLOW','DENY','REQUIRE_APPROVAL')),
  created_at   TIMESTAMPTZ DEFAULT now(),
  UNIQUE (scope_type, scope_id, subject_type, subject_id, perm_key)
);
```

**V1 简化**：
- scope_id 必须存在（FK 暂不加，留 V2）
- subject_type = 'USER' or 'AGENT'
- 唯一约束：同 scope + subject + perm_key 只有一条覆盖

## 7. 端点

```http
GET    /projects/:id/permissions
POST   /projects/:id/permissions
       Body: { subject_type, subject_id, perm_key, effect }
DELETE /projects/:id/permissions/:pid

GET    /channels/:id/permissions
POST   /channels/:id/permissions
DELETE /channels/:id/permissions/:pid
```

权限要求：project/channel owner（`manage_channel` permission）。

## 8. 默认矩阵

```python
DEFAULT_MATRIX = {
    ('USER', 'owner'): {
        'read_message': 'ALLOW',
        'write_message': 'ALLOW',
        'write_memory': 'REQUIRE_APPROVAL',
        'execute_code': 'DENY',
        'create_pr': 'REQUIRE_APPROVAL',
        'approve_memory': 'ALLOW',
        'manage_channel': 'ALLOW',
    },
    ('USER', 'member'): {
        'read_message': 'ALLOW',
        'write_message': 'ALLOW',
        'write_memory': 'REQUIRE_APPROVAL',
        'execute_code': 'DENY',
        'create_pr': 'DENY',
        'approve_memory': 'DENY',
        'manage_channel': 'DENY',
    },
    ('AGENT', 'lifecycle=ACTIVE'): {
        'read_message': 'ALLOW',
        'write_message': 'ALLOW',
        'write_memory': 'REQUIRE_APPROVAL',
        'execute_code': 'DENY',
        'create_pr': 'DENY',
        'approve_memory': 'DENY',
        'manage_channel': 'DENY',
    },
    # 简化：AGENT 不同 lifecycle 用 activity 区分
    # V1 简化：lifecycle ≠ ACTIVE → all DENY
}
```

**V1 简化**：lifecycle ≠ ACTIVE → 全 DENY（即使 cache hit 也走 lifecycle check）。V2 可优化。

## 9. Guard 流程完整示例

### 9.1 Agent 发消息（write_message = ALLOW）

```
1. Agent hello → 鉴权通过
2. Agent POST /channels/:id/messages
3. Guard: checkPermission(agent, 'write_message', channel)
   → cache miss → 重建
   → DEFAULT_MATRIX[(AGENT, write_message)] = ALLOW
   → 写 cache, return ALLOW
4. Guard: ALLOW → next
5. Controller: 写 message
6. response 200
```

### 9.2 Agent 写 memory（write_memory = REQUIRE_APPROVAL → 拒绝）

```
1. Agent POST /memory-proposals
2. Guard: checkPermission(agent, 'write_memory', project)
   → REQUIRE_APPROVAL
3. Guard: REQUIRE_APPROVAL → 403
4. ⚠️ Agent 看到 403，必须改用其他 endpoint
```

**正确路径**：

```
Agent POST /memory-proposals/auto-from-agent
  → Controller 显式调 policy.evaluate
  → policy 返 REQUIRE_APPROVAL
  → Controller 调 E5 proposal 创建路径
  → response 201 { status: 'PROPOSED', proposal_id: '...' }
```

## 10. v0.4.2 修复的安全 Bug

**漏洞（v0.4.1）**：

```ts
// 假设 Guard 收到 REQUIRE_APPROVAL 静默放行
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('write_memory')
async proposeMemory(@Body() body) {
  // 业务层忘记调 policy.evaluate
  // 直接写 memory_items（绕过审批！）
  await db.insert_memory_item(body);
}
```

**修复（v0.4.2）**：

```ts
// Guard 收到 REQUIRE_APPROVAL → 拒绝 403
// 业务层必须显式：
@Post('/memory-proposals/from-agent')
async autoProposeFromAgent(@Body() body) {
  const decision = await policy.evaluate('write_memory', {...});
  if (decision === 'DENY') throw new ForbiddenException();
  if (decision === 'REQUIRE_APPROVAL') {
    // 显式 proposal
    return await db.insert_memory_proposal({...body, status: 'PROPOSED'});
  }
  // ALLOW：直接写
  return await db.insert_memory_item({...body, status: 'APPROVED'});
}
```

**安全保证**：
- 调用栈里如果忘调 `policy.evaluate` → 不会写 memory（只 proposal 路径显式）
- 每个 REQUIRE_APPROVAL 路径都强制业务层显式处理
- 审计：policy.evaluate 调用都被记录

## 11. E2E 验收点

```
e2e/07-permission-orchestration/
  test_001_default_matrix.json
    Given Human owner
    When check write_message
    Then ALLOW

  test_002_channel_override.json
    Given Channel has 'write_message=DENY for Agent X'
    When Agent X writes
    Then 403

  test_003_cache_invalidation.json
    Given Channel override
    When DELETE override
    Then within 1s, Agent can write (cache miss → 重建)

  test_004_multi_instance_invalidation.json
    Given 2 API instances
    When instance A updates permission
    Then instance B's cache invalidated within 1s (via pub/sub)

  test_005_guard_bypass_resistance.json
    When Guard returns REQUIRE_APPROVAL
    Then 403 (NOT silent pass-through)

  test_006_explicit_policy_evaluate.json
    When business endpoint /from-agent called
    Then policy.evaluate called explicitly, audit logged

  test_007_approve_memory_only_owner.json
    Given Human member
    When approve memory
    Then 403 (approve_memory=DENY for member)

  test_008_lifecycle_pause_deny.json
    Given Agent lifecycle=PAUSED
    When write message
    Then 403
```

## 12. 与其他设计的关系

- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（Resolver 调度时用 permission 过滤）
- 详见 [05-memory-approval-flow.md](./05-memory-approval-flow.md)（write_memory 走 E5 proposal 流程）
- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（消息流的 permission 校验）
