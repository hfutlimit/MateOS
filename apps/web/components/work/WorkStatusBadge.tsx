"use client";

import { Tag } from "antd";
import type { WorkItemStatus } from "@/lib/types/work";

// 状态徽标配色与 DS v0.7 §3.1 对齐（lifecycle × activity 状态色）。
// M0 阶段只覆盖 status；activity 维度留到 S3 收口时再补。
const STATUS_STYLE: Record<
  WorkItemStatus,
  { color: string; bg: string; text: string; label: string }
> = {
  OPEN: {
    color: "var(--text-2)",
    bg: "var(--surface-2)",
    text: "var(--text)",
    label: "Open",
  },
  IN_PROGRESS: {
    color: "var(--busy)",
    bg: "var(--busy-bg)",
    text: "var(--busy-text)",
    label: "Working",
  },
  IN_REVIEW: {
    color: "var(--waiting)",
    bg: "var(--waiting-bg)",
    text: "var(--waiting-text)",
    label: "Needs You",
  },
  DONE: {
    color: "var(--free)",
    bg: "var(--free-bg)",
    text: "var(--free-text)",
    label: "Done",
  },
  BLOCKED: {
    color: "var(--error)",
    bg: "var(--error-bg)",
    text: "var(--error-text)",
    label: "Blocked",
  },
  CANCELLED: {
    color: "var(--offline)",
    bg: "var(--surface-2)",
    text: "var(--text-2)",
    label: "Cancelled",
  },
};

export function WorkStatusBadge({ status }: { status: WorkItemStatus }) {
  const s = STATUS_STYLE[status];
  return (
    <span
      style={{
        display: "inline-flex",
        alignItems: "center",
        gap: 6,
        padding: "2px 10px",
        borderRadius: 999,
        background: s.bg,
        color: s.text,
        fontSize: 12,
        fontWeight: 500,
        lineHeight: "20px",
        whiteSpace: "nowrap",
      }}
    >
      <span
        aria-hidden
        style={{
          width: 6,
          height: 6,
          borderRadius: "50%",
          background: s.color,
        }}
      />
      {s.label}
    </span>
  );
}

const TYPE_LABEL: Record<string, string> = {
  TASK: "Task",
  STORY: "Story",
  BUG: "Bug",
  EPIC: "Epic",
};

export function WorkTypeTag({ type }: { type: string }) {
  return (
    <Tag
      style={{
        background: "var(--surface-2)",
        border: "1px solid var(--border)",
        color: "var(--text-2)",
        fontWeight: 500,
        borderRadius: 4,
        margin: 0,
      }}
    >
      {TYPE_LABEL[type] ?? type}
    </Tag>
  );
}
