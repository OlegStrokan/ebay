/** @type {import('next').NextConfig} */
const nextConfig = {
  // Produces .next/standalone — a minimal, self-contained server bundle with
  // only the node_modules actually needed at runtime, so the Docker runtime
  // stage doesn't have to ship the full node_modules tree.
  output: "standalone",
};

export default nextConfig;
