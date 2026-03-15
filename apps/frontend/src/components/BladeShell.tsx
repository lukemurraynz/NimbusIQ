import { type ReactNode, useState, useCallback } from "react";
import {
  OverlayDrawer,
  DrawerHeader,
  DrawerHeaderTitle,
  DrawerBody,
  Button,
  mergeClasses,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { Dismiss24Regular } from "@fluentui/react-icons";
import { BladeContext, type BladeConfig } from "./BladeContext";

const useStyles = makeStyles({
  drawer: {
    "& .fui-DrawerBody": {
      padding: 0,
    },
  },
  fullDrawer: {
    "& .fui-DrawerSurface": {
      width: "calc(100vw - 64px)",
      maxWidth: "calc(100vw - 64px)",
    },
    "@media (max-width: 768px)": {
      "& .fui-DrawerSurface": {
        width: "100vw",
        maxWidth: "100vw",
      },
    },
  },
  bladeContent: {
    height: "100%",
    display: "flex",
    flexDirection: "column",
  },
  bladeHeader: {
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: tokens.spacingVerticalL,
  },
  bladeBody: {
    flex: 1,
    overflow: "auto",
    padding: tokens.spacingHorizontalXL,
  },
});

interface BladeProviderProps {
  children: ReactNode;
}

/**
 * Azure Portal-style blade system using Fluent UI OverlayDrawer.
 * Blades stack to the right and maintain navigation context.
 */
export function BladeProvider({ children }: BladeProviderProps) {
  const styles = useStyles();
  const [blades, setBlades] = useState<BladeConfig[]>([]);

  const openBlade = useCallback((config: BladeConfig) => {
    setBlades((prev) => {
      // Close existing blade with same ID
      const filtered = prev.filter((b) => b.id !== config.id);
      return [...filtered, config];
    });
  }, []);

  const closeBlade = useCallback((id: string) => {
    setBlades((prev) => {
      const blade = prev.find((b) => b.id === id);
      if (blade?.onClose) {
        blade.onClose();
      }
      return prev.filter((b) => b.id !== id);
    });
  }, []);

  const closeAllBlades = useCallback(() => {
    setBlades((prev) => {
      prev.forEach((blade) => {
        if (blade.onClose) {
          blade.onClose();
        }
      });
      return [];
    });
  }, []);

  return (
    <BladeContext.Provider value={{ openBlade, closeBlade, closeAllBlades }}>
      {children}
      {blades.map((blade, index) => (
        <OverlayDrawer
          key={blade.id}
          open
          position="end"
          modalType="non-modal"
          size={blade.size ?? "medium"}
          onOpenChange={(_, data) => {
            if (!data.open) {
              closeBlade(blade.id);
            }
          }}
          className={mergeClasses(
            styles.drawer,
            blade.size === "full" ? styles.fullDrawer : undefined,
          )}
          style={{
            // Stack blades with offset for visual depth
            right: blade.size === "full" ? "0px" : `${index * 32}px`,
            zIndex: 1000 + index,
          }}
        >
          <div className={styles.bladeContent}>
            <DrawerHeader className={styles.bladeHeader}>
              <DrawerHeaderTitle
                action={
                  <Button
                    appearance="subtle"
                    icon={<Dismiss24Regular />}
                    onClick={() => closeBlade(blade.id)}
                  />
                }
              >
                {blade.title}
              </DrawerHeaderTitle>
            </DrawerHeader>
            <DrawerBody className={styles.bladeBody}>
              {blade.content}
            </DrawerBody>
          </div>
        </OverlayDrawer>
      ))}
    </BladeContext.Provider>
  );
}
