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
  metadataBase: new URL("https://continuity.alirezaafshan.com"),
  title: "Codex Continuity — Keep the agents. Replace the window.",
  description:
    "An unofficial Windows utility that keeps Codex agent threads alive while the desktop app updates or restarts.",
  alternates: {
    canonical: "/",
  },
  icons: {
    icon: "/icon.svg",
  },
  openGraph: {
    type: "website",
    title: "Codex Continuity — Keep the agents. Replace the window.",
    description:
      "An unofficial Windows utility that keeps Codex agent threads alive while the desktop app updates or restarts.",
    images: [
      {
        url: "/og.png",
        width: 1200,
        height: 630,
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
      "/og.png",
    ],
  },
  authors: [{ name: "Alireza Afshan", url: "https://alirezaafshan.com" }],
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
        <script
          type="application/ld+json"
          dangerouslySetInnerHTML={{
            __html: JSON.stringify({
              "@context": "https://schema.org",
              "@type": "SoftwareApplication",
              name: "Codex Continuity",
              softwareVersion: "0.8.0",
              applicationCategory: "UtilitiesApplication",
              operatingSystem: "Windows 11 x64",
              description:
                "An unofficial Windows utility that keeps Codex agent threads alive while the desktop app updates or restarts.",
              url: "https://continuity.alirezaafshan.com",
              downloadUrl:
                "https://github.com/YesterdaysLemon/codex-continuity/releases/latest",
              codeRepository:
                "https://github.com/YesterdaysLemon/codex-continuity",
              license:
                "https://github.com/YesterdaysLemon/codex-continuity/blob/main/LICENSE",
              author: {
                "@type": "Person",
                name: "Alireza Afshan",
                url: "https://alirezaafshan.com",
              },
              offers: {
                "@type": "Offer",
                price: "0",
                priceCurrency: "USD",
              },
            }).replace(/</g, "\\u003c"),
          }}
        />
        {children}
      </body>
    </html>
  );
}
