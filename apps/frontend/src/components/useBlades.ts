import { useContext } from 'react';
import { BladeContext } from './BladeContext';

export function useBlades() {
  const context = useContext(BladeContext);
  if (!context) {
    throw new Error('useBlades must be used within BladeProvider');
  }
  return context;
}
