import type { Metadata } from "next";
import { ConfigProvider } from "antd";
import { AppShell } from "@/components/shell/AppShell";
import { QueryProvider } from "@/components/providers/QueryProvider";
import "antd/dist/reset.css";
import "./globals.css";

export const metadata: Metadata = {
  title: "MateOS",
  description: "AI-native team operating system",
};

const antdTheme = {
  token: {
    // 与 tokens.css --primary 对齐（SSOT 仍是 tokens.css，这里只做 antd 注入；
    // 改色时记得先改 tokens.css，再同步这里）
    colorPrimary: "#534AB7",
    colorLink: "#534AB7",
    borderRadius: 6,
    fontFamily:
      'Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif',
  },
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="zh-CN">
      <body>
        <ConfigProvider theme={antdTheme}>
          <QueryProvider>
            <AppShell>{children}</AppShell>
          </QueryProvider>
        </ConfigProvider>
      </body>
    </html>
  );
}
