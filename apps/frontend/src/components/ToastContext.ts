import { createContext } from 'react';

export interface NotifyOptions {
  title: string;
  body?: string;
  intent?: 'info' | 'success' | 'warning' | 'error';
}

export type NotifyFn = (options: NotifyOptions) => void;

export const ToastContext = createContext<NotifyFn>(() => {});
