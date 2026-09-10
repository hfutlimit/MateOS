-- 仅在 PostgreSQL 数据卷**首次初始化**时由 entrypoint 执行。
--
-- 已存在的环境不会重跑此脚本，请手动执行一次：
--   docker exec mateos-postgres psql -U mateos -d mateos -c "CREATE DATABASE mateos_test"
--
-- 集成测试用独立库，避免 TRUNCATE 清掉本地开发数据。
CREATE DATABASE mateos_test;
