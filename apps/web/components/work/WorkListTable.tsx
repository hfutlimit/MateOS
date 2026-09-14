"use client";

import { Table } from "antd";
import type { ColumnsType } from "antd/es/table";
import Link from "next/link";
import type { WorkItemSummary } from "@/lib/types/work";
import { WorkStatusBadge, WorkTypeTag } from "./WorkStatusBadge";

const dateFmt = new Intl.DateTimeFormat("zh-CN", {
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});

const fmt = (ms: number | null) =>
  ms == null ? <span style={{ color: "var(--text-3)" }}>—</span> : dateFmt.format(new Date(ms));

const assigneeLabel = (w: WorkItemSummary) => {
  if (!w.assignee_type) return <span style={{ color: "var(--text-3)" }}>未指派</span>;
  if (w.assignee_type === "HUMAN") return "人类成员";
  return "Agent";
};

const providerLabel = (key: string) =>
  key === "builtin" ? "Built-in" : key;

export function WorkListTable({ items }: { items: WorkItemSummary[] }) {
  const columns: ColumnsType<WorkItemSummary> = [
    {
      title: "Type",
      dataIndex: "type",
      key: "type",
      width: 80,
      render: (t) => <WorkTypeTag type={t} />,
    },
    {
      title: "Title",
      dataIndex: "title",
      key: "title",
      render: (title, row) => (
        <Link
          href={`/projects/${row.project_id}/work/${row.id}`}
          style={{ color: "var(--text)", fontWeight: 500 }}
        >
          {title}
        </Link>
      ),
    },
    {
      title: "Status",
      dataIndex: "status",
      key: "status",
      width: 130,
      render: (s) => <WorkStatusBadge status={s} />,
    },
    {
      title: "Assignee",
      key: "assignee",
      width: 110,
      render: (_, row) => assigneeLabel(row),
    },
    {
      title: "Due",
      dataIndex: "due_at_ms",
      key: "due",
      width: 130,
      render: fmt,
    },
    {
      title: "Updated",
      dataIndex: "updated_at_ms",
      key: "updated",
      width: 130,
      render: fmt,
    },
    {
      title: "Provider",
      dataIndex: "provider_key",
      key: "provider",
      width: 100,
      render: providerLabel,
    },
  ];

  return (
    <Table<WorkItemSummary>
      rowKey="id"
      columns={columns}
      dataSource={items}
      pagination={{ pageSize: 50, showSizeChanger: false }}
      size="middle"
      locale={{ emptyText: "暂无 WorkItem" }}
    />
  );
}
