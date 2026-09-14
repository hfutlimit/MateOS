"use client";

import {
  Breadcrumb,
  Card,
  Divider,
  Space,
  Tag,
  Typography,
  Empty,
  Skeleton,
} from "antd";
import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useParams } from "next/navigation";
import { getWorkItem } from "@/lib/api/work";
import { WorkStatusBadge, WorkTypeTag } from "@/components/work/WorkStatusBadge";

const { Title, Text, Paragraph } = Typography;

const dateFmt = new Intl.DateTimeFormat("zh-CN", {
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});

interface MockComment {
  id: string;
  author: string;
  author_type: "HUMAN" | "AGENT" | "SYSTEM";
  body: string;
  created_at_ms: number;
}

const MOCK_COMMENTS: Record<string, MockComment[]> = {
  // key = workItem.id
  "11111111-1111-1111-1111-111111111111": [
    {
      id: "c-1",
      author: "PO Alice",
      author_type: "HUMAN",
      body: "先把 Stub Agent dispatch 走通，再补 Memory Proposal 申请闭环。",
      created_at_ms: Date.now() - 5 * 60 * 60 * 1000,
    },
    {
      id: "c-2",
      author: "Backend Agent",
      author_type: "AGENT",
      body: "已跑通 dispatch（commit 9fbe627）；Memory Proposal 走通后再 mark done。",
      created_at_ms: Date.now() - 30 * 60 * 1000,
    },
  ],
  "44444444-4444-4444-4444-444444444444": [],
  "55555555-5555-5555-5555-555555555555": [
    {
      id: "c-3",
      author: "Backend Agent",
      author_type: "AGENT",
      body: "已加 `accessible_channel_ids` 校验；integration test 10 条全过。",
      created_at_ms: Date.now() - 1 * 60 * 60 * 1000,
    },
  ],
};

interface MockExecution {
  id: string;
  agent_name: string;
  status: "RUNNING" | "SUCCEEDED" | "FAILED";
  started_at_ms: number;
  completed_at_ms: number | null;
}

const MOCK_EXECUTIONS: Record<string, MockExecution[]> = {
  "11111111-1111-1111-1111-111111111111": [
    {
      id: "exec-1",
      agent_name: "Backend Agent",
      status: "SUCCEEDED",
      started_at_ms: Date.now() - 1 * 60 * 60 * 1000,
      completed_at_ms: Date.now() - 30 * 60 * 1000,
    },
  ],
  "55555555-5555-5555-5555-555555555555": [
    {
      id: "exec-2",
      agent_name: "Backend Agent",
      status: "RUNNING",
      started_at_ms: Date.now() - 10 * 60 * 1000,
      completed_at_ms: null,
    },
  ],
};

export default function WorkDetailPage() {
  const params = useParams<{ projectId: string; id: string }>();

  const { data, isLoading, error } = useQuery({
    queryKey: ["work-item", params.id],
    queryFn: () => getWorkItem(params.id),
  });

  if (isLoading) {
    return (
      <Space direction="vertical" size={16} style={{ width: "100%" }}>
        <Skeleton active paragraph={{ rows: 4 }} />
      </Space>
    );
  }

  if (error || !data) {
    return (
      <Space direction="vertical" size={16} style={{ width: "100%" }}>
        <Breadcrumb
          items={[
            { title: <Link href={`/projects/${params.projectId}/work`}>Work</Link> },
            { title: "未找到" },
          ]}
        />
        <Empty description={error ? `加载失败：${String(error)}` : "WorkItem 不存在"} />
      </Space>
    );
  }

  const comments = MOCK_COMMENTS[data.id] ?? [];
  const executions = MOCK_EXECUTIONS[data.id] ?? [];

  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Breadcrumb
        items={[
          { title: <Link href={`/projects/${params.projectId}/work`}>Work</Link> },
          { title: data.title },
        ]}
      />

      <div
        style={{
          display: "flex",
          alignItems: "baseline",
          justifyContent: "space-between",
          flexWrap: "wrap",
          gap: 12,
        }}
      >
        <div>
          <Space size={10} align="center" style={{ marginBottom: 6 }}>
            <WorkTypeTag type={data.type} />
            <WorkStatusBadge status={data.status} />
            <Tag
              style={{
                background: "var(--surface-2)",
                border: "1px solid var(--border)",
                color: "var(--text-2)",
                borderRadius: 4,
                margin: 0,
              }}
            >
              {data.provider_key === "builtin" ? "Built-in" : data.provider_key}
            </Tag>
          </Space>
          <Title level={3} style={{ margin: 0 }}>
            {data.title}
          </Title>
          <Text style={{ color: "var(--text-3)", fontSize: 12 }}>
            id: {data.id}
          </Text>
        </div>
      </div>

      <div
        style={{
          display: "grid",
          gridTemplateColumns: "minmax(0, 1fr) 320px",
          gap: 16,
        }}
      >
        {/* 左主区 */}
        <Space direction="vertical" size={12} style={{ width: "100%" }}>
          <Card
            bordered
            title="描述"
            style={{
              border: "1px solid var(--border)",
              borderRadius: "var(--radius-box)",
            }}
            styles={{ body: { padding: 16 } }}
          >
            {data.description ? (
              <Paragraph style={{ margin: 0, color: "var(--text-body)" }}>
                {data.description}
              </Paragraph>
            ) : (
              <Text style={{ color: "var(--text-3)" }}>暂无描述</Text>
            )}
          </Card>

          <Card
            bordered
            title="评论"
            style={{
              border: "1px solid var(--border)",
              borderRadius: "var(--radius-box)",
            }}
            styles={{ body: { padding: 16 } }}
          >
            {comments.length === 0 ? (
              <Empty description="暂无评论" image={Empty.PRESENTED_IMAGE_SIMPLE} />
            ) : (
              <Space direction="vertical" size={12} style={{ width: "100%" }}>
                {comments.map((c) => (
                  <div key={c.id}>
                    <Space size={8} align="center">
                      <Text strong style={{ color: "var(--text)" }}>
                        {c.author}
                      </Text>
                      <Text style={{ color: "var(--text-3)", fontSize: 12 }}>
                        · {dateFmt.format(new Date(c.created_at_ms))}
                      </Text>
                    </Space>
                    <Paragraph
                      style={{
                        margin: "4px 0 0 0",
                        color: "var(--text-body)",
                      }}
                    >
                      {c.body}
                    </Paragraph>
                    <Divider style={{ margin: "12px 0" }} />
                  </div>
                ))}
              </Space>
            )}
          </Card>

          <Card
            bordered
            title="Execution 关联"
            style={{
              border: "1px solid var(--border)",
              borderRadius: "var(--radius-box)",
            }}
            styles={{ body: { padding: 16 } }}
          >
            {executions.length === 0 ? (
              <Empty
                description="暂无 Execution 关联（@Agent 派单后会出现）"
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            ) : (
              <Space direction="vertical" size={10} style={{ width: "100%" }}>
                {executions.map((e) => (
                  <div
                    key={e.id}
                    style={{
                      display: "flex",
                      alignItems: "center",
                      justifyContent: "space-between",
                      padding: "8px 12px",
                      border: "1px solid var(--border)",
                      borderRadius: "var(--radius-card)",
                    }}
                  >
                    <div>
                      <Text strong>{e.agent_name}</Text>
                      <Text style={{ color: "var(--text-3)", fontSize: 12, marginLeft: 8 }}>
                        {dateFmt.format(new Date(e.started_at_ms))}
                      </Text>
                    </div>
                    <Tag
                      style={{
                        background:
                          e.status === "SUCCEEDED"
                            ? "var(--free-bg)"
                            : e.status === "RUNNING"
                              ? "var(--busy-bg)"
                              : "var(--error-bg)",
                        color:
                          e.status === "SUCCEEDED"
                            ? "var(--free-text)"
                            : e.status === "RUNNING"
                              ? "var(--busy-text)"
                              : "var(--error-text)",
                        border: 0,
                      }}
                    >
                      {e.status}
                    </Tag>
                  </div>
                ))}
              </Space>
            )}
          </Card>
        </Space>

        {/* 右 sidebar */}
        <Space direction="vertical" size={12} style={{ width: "100%" }}>
          <Card
            bordered
            title="属性"
            style={{
              border: "1px solid var(--border)",
              borderRadius: "var(--radius-box)",
            }}
            styles={{ body: { padding: 16 } }}
          >
            <Space direction="vertical" size={8} style={{ width: "100%" }}>
              <Row k="Assignee">
                {data.assignee_type
                  ? data.assignee_type === "HUMAN"
                    ? "人类成员"
                    : "Agent"
                  : "—"}
              </Row>
              <Row k="Due">{data.due_at_ms ? dateFmt.format(new Date(data.due_at_ms)) : "—"}</Row>
              <Row k="Updated">{dateFmt.format(new Date(data.updated_at_ms))}</Row>
              <Row k="Created">{dateFmt.format(new Date(data.created_at_ms))}</Row>
              <Row k="Binding">{data.binding_id}</Row>
              <Row k="Provider">{data.provider_key}</Row>
              {data.external_ref && <Row k="External Ref">{data.external_ref}</Row>}
            </Space>
          </Card>

          <Card
            bordered
            title="操作"
            style={{
              border: "1px solid var(--border)",
              borderRadius: "var(--radius-box)",
            }}
            styles={{ body: { padding: 16 } }}
          >
            <Text style={{ color: "var(--text-3)", fontSize: 12 }}>
              派给 Agent / 改状态 / 切 Provider 等操作留到 M0+1 接入真实 API。
            </Text>
          </Card>
        </Space>
      </div>
    </Space>
  );
}

function Row({ k, children }: { k: string; children: React.ReactNode }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", gap: 12 }}>
      <Text style={{ color: "var(--text-2)", fontSize: 12 }}>{k}</Text>
      <Text style={{ color: "var(--text)", fontSize: 13, textAlign: "right" }}>{children}</Text>
    </div>
  );
}
