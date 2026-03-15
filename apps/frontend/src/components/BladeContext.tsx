import { createContext, type ReactNode } from 'react';

export interface BladeConfig {
  id: string;
  title: string;
  content: ReactNode;
  size?: 'small' | 'medium' | 'large' | 'full';
  onClose?: () => void;
}

export interface BladeContextValue {
  openBlade: (config: BladeConfig) => void;
  closeBlade: (id: string) => void;
  closeAllBlades: () => void;
}

export const BladeContext = createContext<BladeContextValue | null>(null);
