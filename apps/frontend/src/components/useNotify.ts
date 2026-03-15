import { useContext } from 'react';
import { ToastContext } from './ToastContext';
import type { NotifyFn } from './ToastContext';

export type { NotifyFn };
export function useNotify(): NotifyFn {
  return useContext(ToastContext);
}
