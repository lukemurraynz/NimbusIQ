export type PersonaId =
  | "platform_engineer"
  | "service_owner"
  | "cloud_architect"
  | "engineering_leader";

export interface Persona {
  id: PersonaId;
  label: string;
  description: string;
  requiredRoles: string[];
}

export const PERSONAS: Persona[] = [
  {
    id: "platform_engineer",
    label: "Platform Engineer",
    description:
      "Runs analysis, manages service groups, and administers policy/roles.",
    requiredRoles: ["NimbusIQ.Admin"],
  },
  {
    id: "service_owner",
    label: "Service Owner",
    description:
      "Reviews recommendations and performs approvals with auditability.",
    requiredRoles: ["NimbusIQ.Recommendation.Approve"],
  },
  {
    id: "cloud_architect",
    label: "Cloud Architect",
    description:
      "Interprets findings, evaluates trade-offs, and governs IaC modernization outputs.",
    requiredRoles: ["NimbusIQ.Analysis.Write", "NimbusIQ.Recommendation.Read"],
  },
  {
    id: "engineering_leader",
    label: "Engineering Leader",
    description:
      "Tracks progress and risk over time (scores, trendlines, timeline).",
    requiredRoles: ["NimbusIQ.Analysis.Read", "NimbusIQ.Recommendation.Read"],
  },
];

export function resolvePersonasFromRoles(roles: string[]): Persona[] {
  const roleSet = new Set(roles);
  return PERSONAS.filter((p) => p.requiredRoles.every((r) => roleSet.has(r)));
}
