/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  // antd 在 Next 14 下需要 transpile，避免 ESM/CJS 互操作问题
  transpilePackages: [
    "antd",
    "@ant-design/icons",
    "rc-util",
    "rc-pagination",
    "rc-picker",
    "rc-tree",
    "rc-table",
  ],
};

export default nextConfig;
