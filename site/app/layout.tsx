import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "Codex Continuity — Keep the agents. Replace the window.",
  description:
    "An unofficial Windows utility that keeps Codex agent threads alive while the desktop app updates or restarts.",
  openGraph: {
    type: "website",
    title: "Codex Continuity — Keep the agents. Replace the window.",
    description:
      "An unofficial Windows utility that keeps Codex agent threads alive while the desktop app updates or restarts.",
    images: [
      {
        url: "https://raw.githubusercontent.com/YesterdaysLemon/codex-continuity/main/site/public/og.png",
        width: 1536,
        height: 1024,
        alt: "Codex Continuity — Keep the agents. Replace the window.",
      },
    ],
  },
  twitter: {
    card: "summary_large_image",
    title: "Codex Continuity — Keep the agents. Replace the window.",
    description:
      "Keep Codex agent threads alive while the Windows desktop app updates or restarts.",
    images: [
      "https://raw.githubusercontent.com/YesterdaysLemon/codex-continuity/main/site/public/og.png",
    ],
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body
        className={`${geistSans.variable} ${geistMono.variable} antialiased`}
      >
        {children}
      </body>
    </html>
  );
}
