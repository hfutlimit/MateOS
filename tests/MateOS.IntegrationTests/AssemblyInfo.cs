using Xunit;

// 集成测试共用一个 PostgreSQL 库（mateos_test），而 fixture 在每个用例前
// TRUNCATE 全部业务表。并行跑不同 test class 会出现两类真实故障：
//   ① 40P01 deadlock —— 多个 fixture 实例并发 TRUNCATE 同一批表（已实测复现）；
//   ② 「A 正在写，B 把表清空」—— 断言随机失败，且失败位置每次不同，极难排查。
// 因此整个程序集串行执行。代价是耗时变长，换来确定性。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
