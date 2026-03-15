import { useState, useCallback } from "react";
import {
  Card,
  CardHeader,
  Text,
  Button,
  Input,
  Dropdown,
  Option,
  Badge,
  makeStyles,
  tokens,
  Spinner,
  Divider,
} from "@fluentui/react-components";
import {
  Calculator24Regular,
  Add20Regular,
  Delete20Regular,
} from "@fluentui/react-icons";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  Cell,
  Legend,
} from "recharts";
import {
  controlPlaneApi,
  type ScoreSimulationResult,
  type HypotheticalChange,
} from "../services/controlPlaneApi";

const useStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingHorizontalL,
  },
  changeRow: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    alignItems: "end",
  },
  chartContainer: {
    height: "280px",
    width: "100%",
  },
  resultCard: {
    padding: tokens.spacingHorizontalM,
  },
  deltaGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingVerticalS,
  },
});

const categories = ["Architecture", "FinOps", "Reliability", "Sustainability"];
const changeTypes = [
  "enable_feature",
  "apply_recommendation",
  "remove_resource",
  "add_resource",
  "change_sku",
];

interface ScoreSimulationPanelProps {
  serviceGroupId: string;
  accessToken?: string;
}

export function ScoreSimulationPanel({
  serviceGroupId,
  accessToken,
}: ScoreSimulationPanelProps) {
  const styles = useStyles();
  const [changes, setChanges] = useState<HypotheticalChange[]>([
    {
      changeType: "apply_recommendation",
      category: "Reliability",
      description: "",
      estimatedImpact: 5,
    },
  ]);
  const [result, setResult] = useState<ScoreSimulationResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();

  const addChange = useCallback(() => {
    setChanges((prev) => [
      ...prev,
      {
        changeType: "enable_feature",
        category: "Architecture",
        description: "",
        estimatedImpact: 0,
      },
    ]);
  }, []);

  const removeChange = useCallback((index: number) => {
    setChanges((prev) => prev.filter((_, i) => i !== index));
  }, []);

  const updateChange = useCallback(
    (
      index: number,
      field: keyof HypotheticalChange,
      value: string | number,
    ) => {
      setChanges((prev) =>
        prev.map((c, i) => (i === index ? { ...c, [field]: value } : c)),
      );
    },
    [],
  );

  const runSimulation = useCallback(async () => {
    setLoading(true);
    setError(undefined);
    try {
      const res = await controlPlaneApi.simulateScores(
        { serviceGroupId, hypotheticalChanges: changes },
        accessToken,
      );
      setResult(res);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Simulation failed");
    } finally {
      setLoading(false);
    }
  }, [serviceGroupId, changes, accessToken]);

  const chartData = result
    ? categories.map((cat) => ({
        category: cat,
        current: result.currentScores[cat] ?? 0,
        projected: result.projectedScores[cat] ?? 0,
      }))
    : [];

  return (
    <div className={styles.root}>
      <Card>
        <CardHeader
          image={<Calculator24Regular />}
          header={<Text weight="semibold">What-If Score Simulation</Text>}
          description={
            <Text size={200}>
              Model the impact of changes before implementing them
            </Text>
          }
        />
      </Card>

      {/* Hypothetical Changes */}
      {changes.map((change, idx) => (
        <Card key={idx}>
          <div className={styles.changeRow}>
            <Dropdown
              value={change.changeType}
              onOptionSelect={(_, data) =>
                updateChange(idx, "changeType", data.optionValue ?? "")
              }
              style={{ minWidth: 140 }}
            >
              {changeTypes.map((ct) => (
                <Option key={ct} value={ct}>
                  {ct.replace(/_/g, " ")}
                </Option>
              ))}
            </Dropdown>
            <Dropdown
              value={change.category}
              onOptionSelect={(_, data) =>
                updateChange(idx, "category", data.optionValue ?? "")
              }
              style={{ minWidth: 130 }}
            >
              {categories.map((cat) => (
                <Option key={cat} value={cat}>
                  {cat}
                </Option>
              ))}
            </Dropdown>
            <Input
              aria-label="Description"
              placeholder="Description"
              value={change.description}
              onChange={(_, data) =>
                updateChange(idx, "description", data.value)
              }
              style={{ flex: 1 }}
            />
            <Input
              aria-label="Impact"
              type="number"
              placeholder="±Impact"
              value={String(change.estimatedImpact ?? 0)}
              onChange={(_, data) =>
                updateChange(idx, "estimatedImpact", Number(data.value) || 0)
              }
              style={{ width: 80 }}
            />
            <Button
              appearance="subtle"
              icon={<Delete20Regular />}
              onClick={() => removeChange(idx)}
              disabled={changes.length <= 1}
            />
          </div>
        </Card>
      ))}

      <div style={{ display: "flex", gap: tokens.spacingHorizontalS }}>
        <Button appearance="subtle" icon={<Add20Regular />} onClick={addChange}>
          Add Change
        </Button>
        <Button appearance="primary" onClick={runSimulation} disabled={loading}>
          {loading ? <Spinner size="tiny" /> : "Run Simulation"}
        </Button>
      </div>

      {error && (
        <Text style={{ color: tokens.colorPaletteRedForeground1 }}>
          {error}
        </Text>
      )}

      {/* Results */}
      {result && (
        <>
          <Divider />

          <Card className={styles.resultCard}>
            <CardHeader
              header={<Text weight="semibold">Projected Impact</Text>}
            />
            <Text
              size={200}
              block
              style={{ marginBottom: tokens.spacingVerticalS }}
            >
              {result.reasoning}
            </Text>
            <Badge appearance="outline">
              Confidence: {Math.round(result.confidence * 100)}%
            </Badge>
          </Card>

          <Card>
            <div className={styles.chartContainer}>
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={chartData} layout="vertical">
                  <XAxis type="number" domain={[0, 100]} />
                  <YAxis type="category" dataKey="category" width={100} />
                  <Tooltip />
                  <Legend />
                  <Bar
                    dataKey="current"
                    name="Current"
                    fill="#A0A0A0"
                    barSize={14}
                  >
                    {chartData.map((_, i) => (
                      <Cell key={i} />
                    ))}
                  </Bar>
                  <Bar
                    dataKey="projected"
                    name="Projected"
                    fill="#0078D4"
                    barSize={14}
                  >
                    {chartData.map((_, i) => (
                      <Cell key={i} />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            </div>
          </Card>

          <Card>
            <CardHeader header={<Text weight="semibold">Score Deltas</Text>} />
            <div className={styles.deltaGrid}>
              {categories.map((cat) => {
                const d = result.deltas[cat] ?? 0;
                return (
                  <div
                    key={cat}
                    style={{ display: "flex", justifyContent: "space-between" }}
                  >
                    <Text size={200}>{cat}</Text>
                    <Text
                      size={200}
                      weight="semibold"
                      style={{
                        color:
                          d > 0
                            ? tokens.colorPaletteGreenForeground1
                            : d < 0
                              ? tokens.colorPaletteRedForeground1
                              : undefined,
                      }}
                    >
                      {d > 0 ? "+" : ""}
                      {d}
                    </Text>
                  </div>
                );
              })}
            </div>
          </Card>

          {result.costDeltas && Object.keys(result.costDeltas).length > 0 && (
            <Card>
              <CardHeader header={<Text weight="semibold">Cost Impact</Text>} />
              <div className={styles.deltaGrid}>
                {Object.entries(result.costDeltas).map(([cat, cost]) => (
                  <div
                    key={cat}
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: "2px",
                    }}
                  >
                    <Text size={200} weight="semibold">
                      {cat}
                    </Text>
                    {cost.estimatedMonthlySavings > 0 && (
                      <Text
                        size={200}
                        style={{ color: tokens.colorPaletteGreenForeground1 }}
                      >
                        Saves ${cost.estimatedMonthlySavings.toLocaleString()}
                        /mo
                      </Text>
                    )}
                    {cost.estimatedImplementationCost > 0 && (
                      <Text
                        size={200}
                        style={{ color: tokens.colorPaletteRedForeground1 }}
                      >
                        Cost: $
                        {cost.estimatedImplementationCost.toLocaleString()}
                      </Text>
                    )}
                    <Text size={200}>
                      Net/yr:{" "}
                      <Text
                        weight="semibold"
                        size={200}
                        style={{
                          color:
                            cost.netAnnualImpact >= 0
                              ? tokens.colorPaletteGreenForeground1
                              : tokens.colorPaletteRedForeground1,
                        }}
                      >
                        {cost.netAnnualImpact >= 0 ? "+" : ""}$
                        {cost.netAnnualImpact.toLocaleString()}
                      </Text>
                    </Text>
                  </div>
                ))}
              </div>
            </Card>
          )}

          {result.riskDeltas && Object.keys(result.riskDeltas).length > 0 && (
            <Card>
              <CardHeader
                header={<Text weight="semibold">Risk Assessment</Text>}
              />
              <div className={styles.deltaGrid}>
                {Object.entries(result.riskDeltas).map(([cat, risk]) => (
                  <div
                    key={cat}
                    style={{
                      display: "flex",
                      alignItems: "center",
                      gap: tokens.spacingHorizontalS,
                    }}
                  >
                    <Text size={200}>{cat}</Text>
                    <Badge
                      appearance="filled"
                      color={
                        risk.riskLevel.includes("reduced")
                          ? "success"
                          : risk.riskLevel === "unchanged"
                            ? "informative"
                            : "danger"
                      }
                    >
                      {risk.riskLevel.replace(/_/g, " ")}
                    </Badge>
                    {risk.mitigationNeeded && (
                      <Badge appearance="outline" color="warning">
                        Mitigation needed
                      </Badge>
                    )}
                  </div>
                ))}
              </div>
            </Card>
          )}
        </>
      )}
    </div>
  );
}
