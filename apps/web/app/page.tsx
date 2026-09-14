import { redirect } from "next/navigation";

// 默认入口：DS v0.7 §6 冻结「Needs You / Channels / Work / Team」，Needs You 是默认。
// M0+1 落 Needs You 第一刀后切换到 /needs-you。
export default function Home() {
  redirect("/needs-you");
}
