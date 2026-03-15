import { useEffect, useRef, useState } from "react";
import * as d3 from "d3";
import {
  makeStyles,
  tokens,
  Card,
  Text,
  Button,
  Spinner,
} from "@fluentui/react-components";
import {
  ZoomIn24Regular,
  ZoomOut24Regular,
  ArrowReset24Regular,
} from "@fluentui/react-icons";

const useStyles = makeStyles({
  container: {
    position: "relative",
    width: "100%",
    height: "600px",
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    overflow: "hidden",
  },
  svg: {
    width: "100%",
    height: "100%",
    cursor: "grab",
  },
  controls: {
    position: "absolute",
    top: tokens.spacingVerticalM,
    right: tokens.spacingHorizontalM,
    display: "flex",
    gap: tokens.spacingHorizontalS,
    zIndex: 10,
  },
  legend: {
    position: "absolute",
    bottom: tokens.spacingVerticalM,
    left: tokens.spacingHorizontalM,
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow4,
    zIndex: 10,
  },
  legendItem: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
    marginTop: tokens.spacingVerticalXXS,
  },
  legendDot: {
    width: "12px",
    height: "12px",
    borderRadius: "50%",
  },
  tooltip: {
    position: "absolute",
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusSmall,
    boxShadow: tokens.shadow8,
    pointerEvents: "none",
    zIndex: 20,
    maxWidth: "300px",
  },
  loadingContainer: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    height: "100%",
    gap: tokens.spacingVerticalM,
  },
});

export interface GraphNode {
  id: string;
  name: string;
  type: string;
  metadata?: Record<string, string>;
}

export interface GraphEdge {
  sourceId: string;
  targetId: string;
  type: string;
}

export interface GraphDomain {
  id: string;
  name: string;
  type: string;
  nodeIds: string[];
}

export interface ServiceGraphData {
  nodes: GraphNode[];
  edges: GraphEdge[];
  domains: GraphDomain[];
}

interface ServiceGraphVisualizationProps {
  data?: ServiceGraphData;
  loading?: boolean;
}

interface D3Node extends d3.SimulationNodeDatum {
  id: string;
  name: string;
  type: string;
  metadata?: Record<string, string>;
  domain?: string;
}

interface D3Link extends d3.SimulationLinkDatum<D3Node> {
  type: string;
}

export function ServiceGraphVisualization({
  data,
  loading,
}: ServiceGraphVisualizationProps) {
  const styles = useStyles();
  const svgRef = useRef<SVGSVGElement>(null);
  const [tooltip, setTooltip] = useState<{
    x: number;
    y: number;
    content: React.ReactNode;
  } | null>(null);
  const [transform, setTransform] = useState({ k: 1, x: 0, y: 0 });

  useEffect(() => {
    if (!data || !svgRef.current) return;

    const svg = d3.select(svgRef.current);
    const width = svgRef.current.clientWidth;
    const height = svgRef.current.clientHeight;

    // Clear previous content
    svg.selectAll("*").remove();

    // Create zoom behavior
    const zoom = d3
      .zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.5, 3])
      .on("zoom", (event) => {
        container.attr("transform", event.transform);
        setTransform({
          k: event.transform.k,
          x: event.transform.x,
          y: event.transform.y,
        });
      });

    svg.call(zoom);

    // Create main container
    const container = svg.append("g");

    // Map nodes to domains
    const nodeToDomain = new Map<string, string>();
    data.domains.forEach((domain) => {
      domain.nodeIds.forEach((nodeId) => {
        nodeToDomain.set(nodeId, domain.name);
      });
    });

    // Prepare D3 nodes
    const d3Nodes: D3Node[] = data.nodes.map((node) => ({
      id: node.id,
      name: node.name,
      type: node.type,
      metadata: node.metadata,
      domain: nodeToDomain.get(node.id),
    }));

    // Prepare D3 links
    const d3Links: D3Link[] = data.edges.map((edge) => ({
      source: edge.sourceId,
      target: edge.targetId,
      type: edge.type,
    }));

    // Create force simulation
    const simulation = d3
      .forceSimulation(d3Nodes)
      .force(
        "link",
        d3
          .forceLink<D3Node, D3Link>(d3Links)
          .id((d) => d.id)
          .distance(100),
      )
      .force("charge", d3.forceManyBody().strength(-300))
      .force("center", d3.forceCenter(width / 2, height / 2))
      .force("collision", d3.forceCollide().radius(40));

    // Define node colors by type
    const getNodeColor = (type: string): string => {
      const typeMap: Record<string, string> = {
        Compute: tokens.colorPaletteBlueBorderActive,
        Storage: tokens.colorPaletteGreenBorderActive,
        Network: tokens.colorPalettePurpleBorderActive,
        Database: tokens.colorPaletteMarigoldBorderActive,
        Service: tokens.colorPaletteLightGreenBorderActive,
      };

      for (const [key, color] of Object.entries(typeMap)) {
        if (type.includes(key)) return color;
      }
      return tokens.colorNeutralStroke1;
    };

    // Draw links (edges) with animated data-flow dashes
    const links = container
      .append("g")
      .selectAll("line")
      .data(d3Links)
      .join("line")
      .attr("stroke", tokens.colorNeutralStroke2)
      .attr("stroke-width", 2)
      .attr("stroke-opacity", 0.6)
      .attr("stroke-dasharray", "6 3")
      .attr("marker-end", "url(#arrow)");

    // Animate link dashes
    function animateDashes() {
      links
        .attr("stroke-dashoffset", 0)
        .transition()
        .duration(3000)
        .ease(d3.easeLinear)
        .attr("stroke-dashoffset", -18)
        .on("end", animateDashes);
    }
    animateDashes();

    // Add arrow markers
    svg
      .append("defs")
      .append("marker")
      .attr("id", "arrow")
      .attr("viewBox", "0 -5 10 10")
      .attr("refX", 25)
      .attr("refY", 0)
      .attr("markerWidth", 6)
      .attr("markerHeight", 6)
      .attr("orient", "auto")
      .append("path")
      .attr("d", "M0,-5L10,0L0,5")
      .attr("fill", tokens.colorNeutralStroke2);

    // Draw nodes
    const nodes = container
      .append("g")
      .selectAll<SVGGElement, D3Node>("g")
      .data(d3Nodes)
      .join("g")
      .call(
        d3
          .drag<SVGGElement, D3Node>()
          .on("start", (event, d) => {
            if (!event.active) simulation.alphaTarget(0.3).restart();
            d.fx = d.x;
            d.fy = d.y;
          })
          .on("drag", (event, d) => {
            d.fx = event.x;
            d.fy = event.y;
          })
          .on("end", (event, d) => {
            if (!event.active) simulation.alphaTarget(0);
            d.fx = null;
            d.fy = null;
          }) as unknown as Parameters<
          d3.Selection<SVGGElement, D3Node, SVGGElement, unknown>["call"]
        >[0],
      );

    // Node circles with pulse glow animation
    const glowColor = tokens.colorBrandBackground;
    nodes
      .append("circle")
      .attr("r", 20)
      .attr("fill", (d: D3Node) => getNodeColor(d.type))
      .attr("stroke", tokens.colorNeutralBackground1)
      .attr("stroke-width", 2)
      .style("cursor", "pointer")
      .style("filter", `drop-shadow(0 0 4px ${glowColor})`);

    // Animated glow ring behind each node
    nodes
      .insert("circle", ":first-child")
      .attr("r", 24)
      .attr("fill", "none")
      .attr("stroke", (d: D3Node) => getNodeColor(d.type))
      .attr("stroke-width", 1.5)
      .attr("stroke-opacity", 0)
      .style("pointer-events", "none")
      .each(function () {
        const circle = d3.select(this);
        (function pulse() {
          circle
            .transition()
            .duration(2000)
            .attr("stroke-opacity", 0.5)
            .attr("r", 28)
            .transition()
            .duration(2000)
            .attr("stroke-opacity", 0)
            .attr("r", 24)
            .on("end", pulse);
        })();
      });

    // Node labels
    nodes
      .append("text")
      .text((d: D3Node) =>
        d.name.length > 15 ? d.name.substring(0, 15) + "..." : d.name,
      )
      .attr("text-anchor", "middle")
      .attr("dy", 35)
      .attr("font-size", "11px")
      .attr("fill", tokens.colorNeutralForeground1)
      .style("pointer-events", "none");

    // Node type icons (simplified - using first letter)
    nodes
      .append("text")
      .text((d: D3Node) => d.type.charAt(0).toUpperCase())
      .attr("text-anchor", "middle")
      .attr("dy", 5)
      .attr("font-size", "14px")
      .attr("font-weight", "bold")
      .attr("fill", tokens.colorNeutralBackground1)
      .style("pointer-events", "none");

    // Tooltip on hover with dependency highlighting
    nodes
      .on("mouseenter", (event: MouseEvent, d: D3Node) => {
        d3.select<SVGGElement, D3Node>(event.currentTarget as SVGGElement)
          .select("circle")
          .transition()
          .duration(200)
          .attr("r", 25)
          .attr("stroke-width", 3);

        // Highlight connected links
        links
          .transition()
          .duration(200)
          .attr("stroke-opacity", (l: D3Link) => {
            const src = (l.source as D3Node).id ?? l.source;
            const tgt = (l.target as D3Node).id ?? l.target;
            return src === d.id || tgt === d.id ? 1 : 0.15;
          })
          .attr("stroke-width", (l: D3Link) => {
            const src = (l.source as D3Node).id ?? l.source;
            const tgt = (l.target as D3Node).id ?? l.target;
            return src === d.id || tgt === d.id ? 3 : 2;
          });

        // Dim unconnected nodes
        const connectedIds = new Set<string>([d.id]);
        d3Links.forEach((l) => {
          const src = (l.source as D3Node).id ?? (l.source as string);
          const tgt = (l.target as D3Node).id ?? (l.target as string);
          if (src === d.id) connectedIds.add(tgt);
          if (tgt === d.id) connectedIds.add(src);
        });
        nodes
          .transition()
          .duration(200)
          .style("opacity", (n: D3Node) => (connectedIds.has(n.id) ? 1 : 0.25));

        const tooltipContent = (
          <div style={{ fontSize: "12px" }}>
            <Text weight="semibold" block>
              {d.name}
            </Text>
            <Text size={200} block style={{ marginTop: "4px" }}>
              Type: {d.type}
            </Text>
            {d.domain && (
              <Text size={200} block>
                Domain: {d.domain}
              </Text>
            )}
            {d.metadata?.location && (
              <Text size={200} block>
                Location: {d.metadata.location}
              </Text>
            )}
          </div>
        );

        setTooltip({
          x: event.pageX + 10,
          y: event.pageY + 10,
          content: tooltipContent,
        });
      })
      .on("mouseleave", (event: MouseEvent) => {
        d3.select<SVGGElement, D3Node>(event.currentTarget as SVGGElement)
          .select("circle")
          .transition()
          .duration(200)
          .attr("r", 20)
          .attr("stroke-width", 2);

        // Restore links
        links
          .transition()
          .duration(200)
          .attr("stroke-opacity", 0.6)
          .attr("stroke-width", 2);

        // Restore nodes
        nodes.transition().duration(200).style("opacity", 1);

        setTooltip(null);
      });

    // Update positions on simulation tick
    simulation.on("tick", () => {
      links
        .attr("x1", (d: D3Link) => (d.source as D3Node).x ?? 0)
        .attr("y1", (d: D3Link) => (d.source as D3Node).y ?? 0)
        .attr("x2", (d: D3Link) => (d.target as D3Node).x ?? 0)
        .attr("y2", (d: D3Link) => (d.target as D3Node).y ?? 0);

      nodes.attr(
        "transform",
        (d: D3Node) => `translate(${d.x ?? 0},${d.y ?? 0})`,
      );
    });

    // Cleanup
    return () => {
      svg.on(".zoom", null);
      simulation.stop();
    };
  }, [data]);

  const handleZoomIn = () => {
    if (!svgRef.current) return;
    const svg = d3.select(svgRef.current);
    svg
      .transition()
      .duration(300)
      .call(d3.zoom<SVGSVGElement, unknown>().scaleBy as never, 1.3);
  };

  const handleZoomOut = () => {
    if (!svgRef.current) return;
    const svg = d3.select(svgRef.current);
    svg
      .transition()
      .duration(300)
      .call(d3.zoom<SVGSVGElement, unknown>().scaleBy as never, 1 / 1.3);
  };

  const handleReset = () => {
    if (!svgRef.current) return;
    const svg = d3.select(svgRef.current);
    svg
      .transition()
      .duration(300)
      .call(
        d3.zoom<SVGSVGElement, unknown>().transform as never,
        d3.zoomIdentity,
      );
  };

  if (loading) {
    return (
      <Card className={styles.container}>
        <div className={styles.loadingContainer}>
          <Spinner size="large" label="Loading service graph..." />
        </div>
      </Card>
    );
  }

  if (!data || data.nodes.length === 0) {
    return (
      <Card className={styles.container}>
        <div className={styles.loadingContainer}>
          <Text>No graph data available</Text>
        </div>
      </Card>
    );
  }

  return (
    <Card className={styles.container}>
      <div className={styles.controls}>
        <Button
          size="small"
          icon={<ZoomIn24Regular />}
          onClick={handleZoomIn}
          title="Zoom In"
        />
        <Button
          size="small"
          icon={<ZoomOut24Regular />}
          onClick={handleZoomOut}
          title="Zoom Out"
        />
        <Button
          size="small"
          icon={<ArrowReset24Regular />}
          onClick={handleReset}
          title="Reset View"
        />
      </div>

      <div className={styles.legend}>
        <Text
          size={200}
          weight="semibold"
          block
          style={{ marginBottom: "8px" }}
        >
          Node Types
        </Text>
        {[
          { label: "Compute", color: tokens.colorPaletteBlueBorderActive },
          { label: "Storage", color: tokens.colorPaletteGreenBorderActive },
          { label: "Network", color: tokens.colorPalettePurpleBorderActive },
          { label: "Database", color: tokens.colorPaletteMarigoldBorderActive },
        ].map((item) => (
          <div key={item.label} className={styles.legendItem}>
            <div
              className={styles.legendDot}
              style={{ backgroundColor: item.color }}
            />
            <Text size={200}>{item.label}</Text>
          </div>
        ))}
        <div
          style={{
            marginTop: "12px",
            paddingTop: "8px",
            borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
          }}
        >
          <Text size={200}>Zoom: {Math.round(transform.k * 100)}%</Text>
        </div>
      </div>

      <svg ref={svgRef} className={styles.svg} />

      {tooltip && (
        <Card
          className={styles.tooltip}
          style={{ left: tooltip.x, top: tooltip.y }}
        >
          {tooltip.content}
        </Card>
      )}
    </Card>
  );
}
