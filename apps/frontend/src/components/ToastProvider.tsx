import { useCallback, useId, type ReactNode } from 'react';
import {
  Toaster,
  useToastController,
  Toast,
  ToastTitle,
  ToastBody,
} from '@fluentui/react-components';
import { ToastContext } from './ToastContext';
import type { NotifyOptions } from './ToastContext';

export function ToastProvider({ children }: { children: ReactNode }) {
  const toasterId = useId();
  const { dispatchToast } = useToastController(toasterId);

  const notify = useCallback(
    ({ title, body, intent = 'info' }: NotifyOptions) => {
      dispatchToast(
        <Toast>
          <ToastTitle>{title}</ToastTitle>
          {body && <ToastBody>{body}</ToastBody>}
        </Toast>,
        { intent, timeout: intent === 'error' ? 8000 : 5000, position: 'top-end' }
      );
    },
    [dispatchToast]
  );

  return (
    <ToastContext.Provider value={notify}>
      <Toaster toasterId={toasterId} />
      {children}
    </ToastContext.Provider>
  );
}
