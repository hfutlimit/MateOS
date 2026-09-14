"use client";

import { Segmented, Space, Tag, Typography } from "antd";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { listNeedsYou } from "@/lib/api/needs-you";
import { NeedsYouCard } from "@/components/needs-you/NeedsYouCard";
import type { NeedsYouCategory } from "@/lib/types/needs-you";

const { Title, Text } = Typography;

type CategoryFilter = "ALL" | NeedsYouCategory;

const FILTERS: { value: CategoryFilter; label: string }[] = [
  { value: "ALL", label: "全部" },
  { value: "DECISION", label: "Decision" },
  { value: "INFORMATION", label: "Information" },
  { value: "APPROVAL", label: "Approval" },
  { value: "PROBLEMS", label: "Problems" },
];

export default function NeedsYouPage() {
  const [filter, setFilter] = useState<CategoryFilter>("ALL");

  // M0 阶段 userId 写死 "me"；切真实 API 时从 auth store 拿
  const { data, isLoading, error } = useQuery({
    queryKey: ["needs-you", filter],
    queryFn: () =>
      listNeedsYou("me", {
        category: filter === "ALL" ? undefined : filter,
      }),
  });

  const items = data ?? [];
  // 顶栏计数：按 category 桶
  const counts: Record<NeedsYouCategory, number> = {
    DECISION: 0,
    INFORMATION: 0,
    APPROVAL: 0,
    PROBLEMS: 0,
  };
  for (const it of items) counts[it.category]++;

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
            Needs You
          </Title>
          <Text style={{ color: "var(--text-2)" }}>
            汇聚 CR · Memory · Work · Agent · 预算 五类投影；按 DS v0.7 §1「先给结果与状态」原则展示。
          </Text>
        </div>
        <Space size={6} wrap>
          <Tag color="purple">Decision · {counts.DECISION}</Tag>
          <Tag color="blue">Information · {counts.INFORMATION}</Tag>
          <Tag color="orange">Approval · {counts.APPROVAL}</Tag>
          <Tag color="red">Problems · {counts.PROBLEMS}</Tag>
        </Space>
      </div>

      <Segmented<CategoryFilter>
        options={FILTERS}
        value={filter}
        onChange={(v) => setFilter(v)}
      />

      {isLoading ? (
        <Text type="secondary">加载中…</Text>
      ) : error ? (
        <Text type="danger">加载失败：{String(error)}</Text>
      ) : items.length === 0 ? (
        <Text style={{ color: "var(--text-2)" }}>暂无需要你处理的事项。</Text>
      ) : (
        <Space direction="vertical" size={12} style={{ width: "100%" }}>
          {items.map((it) => (
            <NeedsYouCard key={it.id} item={it} />
          ))}
        </Space>
      )}
    </Space>
  );
}
