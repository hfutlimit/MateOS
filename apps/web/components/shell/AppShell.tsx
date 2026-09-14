"use client";

import { Layout, Menu } from "antd";
import {
  AlertOutlined,
  MessageOutlined,
  CheckSquareOutlined,
  TeamOutlined,
} from "@ant-design/icons";
import { usePathname, useRouter } from "next/navigation";
import type { ReactNode } from "react";

const { Sider, Content, Header } = Layout;

const NAV = [
  { key: "/needs-you", label: "Needs You", icon: <AlertOutlined />, href: "/needs-you" },
  { key: "/channels", label: "Channels", icon: <MessageOutlined />, href: "/channels" },
  {
    key: "/projects/",
    label: "Work",
    icon: <CheckSquareOutlined />,
    // Phase M0 跳到 demo project；auth + project switcher 落地后改为最后访问的 project。
    href: "/projects/demo/work",
  },
  { key: "/team", label: "Team", icon: <TeamOutlined />, href: "/team" },
];

export function AppShell({ children }: { children: ReactNode }) {
  const pathname = usePathname() ?? "/";
  const router = useRouter();

  // 高亮当前：匹配最长前缀
  const active =
    NAV.slice()
      .sort((a, b) => b.key.length - a.key.length)
      .find((n) => pathname.startsWith(n.key))?.key ?? "/needs-you";

  return (
    <Layout style={{ minHeight: "100vh" }}>
      <Sider
        width={208}
        style={{
          background: "var(--surface)",
          borderRight: "1px solid var(--border)",
        }}
      >
        <div
          style={{
            padding: "20px 24px",
            fontSize: 16,
            fontWeight: 600,
            color: "var(--primary)",
            letterSpacing: 0.4,
          }}
        >
          MateOS
        </div>
        <Menu
          mode="inline"
          selectedKeys={[active]}
          style={{ background: "transparent", borderRight: 0 }}
          items={NAV.map((n) => ({
            key: n.key,
            icon: n.icon,
            label: n.label,
            onClick: () => router.push(n.href),
          }))}
        />
      </Sider>
      <Layout>
        <Header
          style={{
            background: "var(--bg)",
            borderBottom: "1px solid var(--border)",
            padding: "0 24px",
            lineHeight: "48px",
            color: "var(--text-2)",
            fontSize: 13,
          }}
        >
          {pathname.startsWith("/projects/")
            ? "Project · Work"
            : active}
        </Header>
        <Content
          style={{
            padding: 24,
            background: "var(--bg)",
          }}
        >
          {children}
        </Content>
      </Layout>
    </Layout>
  );
}
