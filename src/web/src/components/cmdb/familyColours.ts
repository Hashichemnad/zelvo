import type { CiFamily } from "@/lib/cmdb";

/** The CSS variable each family's colour comes from (globals.css, light and dark). */
export const familyColourVariables: Record<CiFamily | "People", string> = {
  BusinessService: "--color-family-business",
  Application: "--color-family-application",
  Software: "--color-family-software",
  Hardware: "--color-family-hardware",
  Network: "--color-family-network",
  CloudResource: "--color-family-cloud",
  People: "--color-family-people",
};
