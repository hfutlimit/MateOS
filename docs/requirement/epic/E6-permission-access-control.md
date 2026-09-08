# E6 · Permission & Access Control

| 字段 | 值 |
| --- | --- |
| Epic ID | E6 |
| 标题 | Permission & Access Control |
| 阶段 | MVP（M4 同步） |
| 上游 | PRD v0.3 §6 / SYSTEM_DESIGN v0.2 §7 |
| 下游 | E1（subject）、E2（can_execute/can_review）、E3（channel scope）、E4（permission 决策依据）、E5（write_memory）、E7（execute_code） |
| 状态 | Draft |

## 1. 背景与动机

Permission Model 是 MateOS 安全模型的核心。所有模块的"谁能做什么"统一走 E6 的 `check()` 函数，避免权限逻辑散落各处。本 epic：

- 7 个权限键（read_message / write_message / write_memory / execute_code / create_pr / approve_memory / manage_channel）
- 三层覆盖：默认矩阵 → Project 覆盖 → Channel 覆盖
- 同步纯函数判定 + Redis 缓存 + 失效广播
- Guard 拦截（NestJS Guard）

## 2. 范围

### 2.1 In Scope

- 7 个权限键
- 默认权限矩阵（code 内置）
- Project 级 / Channel 级覆盖
- `check(subject, perm, scope)` 同步纯函数
- Redis 缓存（5min TTL）+ `perm.changed` pub/sub 失效
- NestJS Guard `@RequirePermission('perm_key')`
- UI：Project 设置页权限矩阵（待做，MVP 后置）

### 2.2 Out of Scope

- 角色继承（RBAC 层级）— V1 简化为 scope 级覆盖，V2 引入角色模板
- 临时权限 / 邀请链接 — V2+
- 资源级权限（消息级 / 记忆级 owner check）— V1 单独 owner 字段控制

## 3. 数据模型

```sql
-- permissions
CREATE TABLE permissions (
  id           UUID PRIMARY KEY,
  scope_type   TEXT NOT NULL CHECK (scope_type IN ('PROJECT','CHANNEL')),
  scope_id     UUID NOT NULL,
  subject_type TEXT NOT NULL CHECK (subject_type IN ('USER','AGENT')),
  subject_id   UUID NOT NULL,
  perm_key     TEXT NOT NULL CHECK (perm_key IN (
                'read_message','write_message','write_memory',
                'execute_code','create_pr','approve_memory','manage_channel')),
  effect       TEXT NOT NULL CHECK (effect IN ('ALLOW','DENY')),
  created_at   TIMESTAMPTZ DEFAULT now(),
  UNIQUE (scope_type, scope_id, subject_type, subject_id, perm_key)
);
CREATE INDEX idx_perm_scope ON permissions(scope_type, scope_id);
CREATE INDEX idx_perm_subject ON permissions(subject_type, subject_id);

-- perm:{scope_type}:{scope_id}:{subject_type}:{subject_id}（Redis Hash）
--   field=perm_key, value=ALLOW|DENY
--   TTL 5min，事件 perm.changed 失效
```

### 3.1 默认权限矩阵（code 内置）

| 权限 | Human (owner) | Human (member) | Agent |
| --- | --- | --- | --- |
| `read_message` | ALLOW | ALLOW | ALLOW（需先加入 channel） |
| `write_message` | ALLOW | ALLOW | ALLOW（need_decision=false） |
| `write_memory` | REQUEST | REQUEST | REQUEST（人审门禁） |
| `execute_code` | DENY | DENY | DENY（默认） |
| `create_pr` | REQUEST | DENY | DENY |
| `approve_memory` | ALLOW | DENY | DENY |
| `manage_channel` | ALLOW | DENY | DENY |

> 注：REQUEST 表示需要走 E5 人审门禁；ALLOW/DENY 是 sync check 的二选一。

## 4. API

### 4.1 REST

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/projects/:id/permissions` | 列出覆盖 | project owner |
| POST | `/projects/:id/permissions` | 新增覆盖 | project owner |
| DELETE | `/projects/:id/permissions/:pid` | 删除覆盖 | project owner |
| GET | `/channels/:id/permissions` | 列出覆盖 | channel owner |
| POST | `/channels/:id/permissions` | 新增覆盖 | channel owner |
| POST | `/check`（内部） | 单次 sync check | service token |

### 4.2 WebSocket

- 无（sync check 是 service-to-service）

### 4.3 内部 API

```ts
// packages/contracts
type PermKey = 'read_message' | 'write_message' | 'write_memory' | 'execute_code' | 'create_pr' | 'approve_memory' | 'manage_channel';

function check(
  subject: MemberRef,
  perm: PermKey,
  scope: { type: 'PROJECT' | 'CHANNEL', id: string }
): 'ALLOW' | 'REQUEST' | 'DENY';
```

## 5. 关键流程

### 5.1 判定流程

```
check(subject, perm, scope):
  1. 查 Redis 缓存 perm:{scope_type}:{scope_id}:{subject_type}:{subject_id}
     命中 → 解析 hash，遍历 perm_key 找匹配 effect
     未命中 → 走重建
  2. 重建（缓存 miss）：
     a) 查 Channel 级覆盖
     b) 查 Project 级覆盖
     c) 取默认矩阵
     d) 合并顺序：Channel > Project > 默认
     e) 写 Redis Hash
  3. 返回 effect
```

### 5.2 缓存失效

```
写覆盖（POST/DELETE /permissions）：
  1. DB 写
  2. Redis DEL perm:{scope_type}:{scope_id}:{subject_type}:{subject_id}
  3. Redis Pub/Sub 广播 perm.changed { scope_type, scope_id, subject_type, subject_id }
  4. 所有 API 实例监听 → 立即清本地 LRU（如有）
```

### 5.3 Guard 拦截

```ts
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('write_message')
@Post('/channels/:id/messages')
async sendMessage(...) { ... }
```

Guard 流程：
1. JwtGuard 解析 token → 写入 req.user
2. PermissionGuard 从 req.params 取 scope
3. 调 check(req.user, perm_key, scope)
4. ALLOW → next
5. REQUEST → 业务层处理（如 write_memory 走 E5 流程）
6. DENY → 403

### 5.4 Agent 特殊路径

- Agent 6 态过滤：OFFLINE/ERROR 不进入 Resolver 候选（E4）
- 写消息时 Guard 只看 channel 成员资格 + perm
- `execute_code` 永久 DENY（V1 Non Goal）

## 6. UI

### 6.1 页面

- 暂不单独做（V1 复用 Project Settings 页一节，MVP 期间可后置）
- Agent 不可见 manage_channel/approve_memory 按钮（自动隐藏）

### 6.2 状态

- 403：toast「无权限」（不弹敏感信息）
- REQUEST：业务层走对应门禁（E5 写记忆、E4 决策 Need Context）

## 7. 验收标准

### 7.1 功能

- **F1** 默认矩阵：Human owner 写消息 ALLOW；Human member 写消息 ALLOW；Agent 写消息 ALLOW（加入 channel 后）
- **F2** Channel 覆盖优先：在 Channel 设 `write_message=DENY for Agent X`，立即生效（缓存失效）
- **F3** Project 覆盖被 Channel 覆盖更优先
- **F4** 写覆盖后 Redis 立即清缓存，pub/sub 广播
- **F5** REQUEST 走业务门禁，不被 Guard 拒绝
- **F6** 跨实例：API 节点 A 写覆盖，节点 B 缓存 1s 内失效（通过 pub/sub）
- **F7** `approve_memory` 只 ALLOW 给 project owner

### 7.2 E2E

- `e2e/E6-001-default-matrix`：7 个权限 × 3 类主体（Human owner / member / Agent）的默认行为
- `e2e/E6-002-channel-override`：覆盖后立即生效（缓存失效）
- `e2e/E6-003-multi-instance`：两个 API 实例，write 触发另一实例缓存清
- `e2e/E6-004-write-memory-gate`：Agent 写记忆走 E5 流程而非 403
- `e2e/E6-005-approval-only-owner`：member 尝试 approve_memory 返 403

### 7.3 非功能

- `check()` P99 < 5ms（含 Redis miss 重建路径）
- 缓存命中率 > 95%（稳态下）
- 1000 并发 check 无锁竞争（用 Lua / atomic GET）

## 8. 与其他 Epic 的关系

- **被依赖**：E1/E2/E3/E4/E5/E7 全部 Guard 调用
- **依赖**：E1（subject 是 User/Agent，scope 是 Project/Channel）
- **冲突裁决**：三层合并顺序 Channel > Project > 默认（SD §7 硬性）

## 9. 风险与开放问题

- **R1**：Redis pub/sub 在跨可用区有秒级延迟 — 5min TTL 是兜底
- **R2**：权限继承（org→team→project→channel）— V1 不引入，避免递归
- **R3**：write_memory = REQUEST 的语义：不是"需要人审"就是拒绝，而是"走 E5 流程" — UI/文档必须明确
- **R4**：资源级权限（如 Personal Memory 仅 owner 可见）— 通过 owner_user_id 字段单独控制，不走 perm 表

## 10. 实施顺序（M4 同步，与 E4 并行）

1. 默认矩阵 + `check()` 纯函数（packages/contracts）
2. Redis 缓存层 + Lua 原子性
3. permissions 表 + REST
4. pub/sub 失效广播
5. NestJS Guard 实现
6. UI（V1 后置）
7. E2E 套件
