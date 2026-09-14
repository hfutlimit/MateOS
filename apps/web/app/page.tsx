import { redirect } from "next/navigation";

// 默认入口：DS v0.7 冻结的"导航（MVP）：Needs You / Channels / Work / Team"。
// Work 页面是当前已有可点开内容；其它三个入口在后续 commit 补齐。
export default function Home() {
  redirect("/projects/demo/work");
}
