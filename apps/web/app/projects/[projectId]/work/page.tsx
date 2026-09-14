"use client";

import { Card, Segmented, Space, Tag, Typography } from "antd";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { listWorkItems } from "@/lib/api/work";
import { WorkListTable } from "@/components/work/WorkListTable";
import type { WorkItemStatus } from "@/lib/types/work";

const { Title, Text } = Typography;

type StatusFilter = "ALL" | WorkItemStatus;

const FILTERS: { value: StatusFilter; label: string }[] = [
  { value: "ALL", label: "全部" },
  { value: "OPEN", label: "Open" },
  { value: "IN_PROGRESS", label: "Working" },
  { value: "IN_REVIEW", label: "Needs You" },
  { value: "DONE", label: "Done" },
  { value: "BLOCKED", label: "Blocked" },
];

interface WorkPageProps {
  params: { projectId: string };
}

export default function WorkPage({ params }: WorkPageProps) {
  const [filter, setFilter] = useState<StatusFilter>("ALL");

  const { data, isLoading, error } = useQuery({
    // queryKey 用 projectId + filter；切 project / 切 tab 自动 refetch
    queryKey: ["work-items", params.projectId, filter],
    queryFn: () =>
      listWorkItems(params.projectId, {
        status: filter === "ALL" ? undefined : filter,
      }),
  });

  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
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
          <Title level={3} style={{ margin: 0 }}>
            Work
          </Title>
          <Text type="secondary" style={{ color: "var(--text-2)" }}>
            项目内的所有 WorkItem；与 Execution / Provider 关联的入口。
          </Text>
        </div>
        <Space size={8}>
          <Tag
            style={{
              background: "var(--primary-50)",
              border: "1px solid var(--primary-100)",
              color: "var(--primary)",
            }}
          >
            Phase M0 · mock data
          </Tag>
        </Space>
      </div>

      <Card
        bordered
        style={{
          background: "var(--bg)",
          border: "1px solid var(--border)",
          borderRadius: "var(--radius-box)",
        }}
        bodyStyle={{ padding: 16 }}
      >
        <Space
          direction="vertical"
          size={12}
          style={{ width: "100%" }}
        >
          <Segmented<StatusFilter>
            options={FILTERS}
            value={filter}
            onChange={(v) => setFilter(v)}
          />
          {isLoading ? (
            <Text type="secondary">加载中…</Text>
          ) : error ? (
            <Text type="danger">加载失败：{String(error)}</Text>
          ) : (
            <WorkListTable items={data ?? []} />
          )}
        </Space>
      </Card>
    </Space>
  );
}
