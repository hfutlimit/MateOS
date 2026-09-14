"use client";

import { Button, Space, Tag, Typography } from "antd";
import {
  CheckOutlined,
  CloseOutlined,
  EyeOutlined,
  MessageOutlined,
  CheckCircleOutlined,
} from "@ant-design/icons";
import type { NeedsYouAction, NeedsYouItem } from "@/lib/types/needs-you";

const { Text, Paragraph } = Typography;

const CATEGORY_LABEL: Record<NeedsYouItem["category"], string> = {
  DECISION: "Decision",
  INFORMATION: "Information",
  APPROVAL: "Approval",
  PROBLEMS: "Problem",
};

// 4 分类配色：与 DS v0.7 §3.1 状态色对齐语义（不是状态点，但同样的色相有助于视觉关联）
const CATEGORY_COLOR: Record<NeedsYouItem["category"], { bg: string; fg: string; border: string }> = {
  DECISION: {
    bg: "var(--thinking)",
    fg: "#FFFFFF",
    border: "var(--thinking)",
  },
  INFORMATION: {
    bg: "var(--primary-50)",
    fg: "var(--primary)",
    border: "var(--primary-100)",
  },
  APPROVAL: {
    bg: "var(--waiting-bg)",
    fg: "var(--waiting-text)",
    border: "var(--waiting)",
  },
  PROBLEMS: {
    bg: "var(--error-bg)",
    fg: "var(--error-text)",
    border: "var(--error)",
  },
};

const URGENCY_LABEL: Record<NeedsYouItem["urgency"], { label: string; color: string }> = {
  WHEN_YOU_CAN: { label: "有空再看", color: "var(--text-2)" },
  TODAY: { label: "今天内", color: "var(--waiting-text)" },
  BLOCKING: { label: "阻塞中", color: "var(--error-text)" },
};

const ACTION_ICON: Record<NeedsYouAction["kind"], React.ReactNode> = {
  VIEW_DETAIL: <EyeOutlined />,
  APPROVE: <CheckOutlined />,
  REJECT: <CloseOutlined />,
  REPLY: <MessageOutlined />,
  RESOLVE: <CheckCircleOutlined />,
};

const ACTION_PRIMARY: Record<NeedsYouAction["kind"], boolean> = {
  // 真正"把人推下去做"的按钮为 primary；其它为 default
  VIEW_DETAIL: false,
  APPROVE: true,
  REJECT: false,
  REPLY: true,
  RESOLVE: false,
};

const RAISED_BY_ICON: Record<NeedsYouItem["raised_by"]["actor_type"], string> = {
  HUMAN: "👤",
  AGENT: "🤖",
  SYSTEM: "⚙️",
};

const dateFmt = new Intl.DateTimeFormat("zh-CN", {
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});

export function NeedsYouCard({ item }: { item: NeedsYouItem }) {
  const cat = CATEGORY_COLOR[item.category];
  const urg = URGENCY_LABEL[item.urgency];
  const raisedBy =
    item.raised_by.actor_type === "SYSTEM"
      ? "MateOS"
      : `${RAISED_BY_ICON[item.raised_by.actor_type] ?? ""} ${item.raised_by.display_name}`;

  return (
    <div
      style={{
        background: "var(--bg)",
        border: "1px solid var(--border)",
        borderRadius: "var(--radius-box)",
        padding: 16,
        display: "flex",
        flexDirection: "column",
        gap: 10,
      }}
    >
      <Space size={8} wrap>
        <Tag
          style={{
            background: cat.bg,
            color: cat.fg,
            border: `1px solid ${cat.border}`,
            fontWeight: 500,
            borderRadius: 4,
            margin: 0,
          }}
        >
          {CATEGORY_LABEL[item.category]}
        </Tag>
        <Text style={{ color: urg.color, fontSize: 12, fontWeight: 500 }}>
          · {urg.label}
        </Text>
        <Text style={{ color: "var(--text-3)", fontSize: 12 }}>
          · {dateFmt.format(new Date(item.raised_at_ms))}
        </Text>
        <Text style={{ color: "var(--text-3)", fontSize: 12 }}>· {raisedBy}</Text>
      </Space>

      <Text
        strong
        style={{
          color: "var(--text)",
          fontSize: 15,
          lineHeight: 1.4,
        }}
      >
        {item.title}
      </Text>

      <Paragraph
        style={{
          color: "var(--text-2)",
          fontSize: 13,
          margin: 0,
          lineHeight: 1.55,
        }}
      >
        {item.reason}
      </Paragraph>

      <Space size={8} wrap style={{ marginTop: 4 }}>
        {item.actions.map((a) => (
          <Button
            key={a.kind + a.label}
            type={ACTION_PRIMARY[a.kind] ? "primary" : "default"}
            size="small"
            icon={ACTION_ICON[a.kind]}
            href={a.href}
            // M0 阶段：href 是占位 "#action-xxx"；下一切真实 API 时改 onClick + mutation
            onClick={(e) => {
              if (a.href.startsWith("#")) e.preventDefault();
            }}
          >
            {a.label}
          </Button>
        ))}
      </Space>
    </div>
  );
}
