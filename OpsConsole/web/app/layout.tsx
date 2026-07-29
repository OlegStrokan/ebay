import type { ReactNode } from "react";
import "./globals.css";
import { getSessionEmail } from "@/lib/session";
import { AccountBar } from "./AccountBar";

export const metadata = {
  title: "Ops Console",
  description: "Saga monitoring dashboard",
};

export default async function RootLayout({ children }: { children: ReactNode }) {
  const email = await getSessionEmail();

  return (
    <html lang="en">
      <body>
        {email && <AccountBar email={email} />}
        {children}
      </body>
    </html>
  );
}
