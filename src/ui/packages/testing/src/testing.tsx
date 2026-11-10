export const packageName = '@watchpoint/testing';

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderOptions, type RenderResult } from '@testing-library/react';
import { initI18n, type SupportedCulture } from '@watchpoint/i18n';
import { applyTheme, type ThemeName } from '@watchpoint/ui';
import { type ReactElement, type ReactNode, useEffect, useState } from 'react';

export interface RenderWithProvidersOptions extends Omit<RenderOptions, 'wrapper'> {
  culture?: SupportedCulture;
  theme?: ThemeName;
  queryClient?: QueryClient;
  session?: unknown;
  live?: unknown;
}

export interface FakeLiveSubscription<TItem = unknown, TDelta = unknown> {
  topic: string;
  filter: unknown;
  handlers: {
    onSnapshot: (items: TItem[], sequence: number, complete: boolean) => void;
    onDelta: (delta: TDelta, sequence: number) => void;
    onResync: (reason: string) => void;
  };
  lastSequence: number;
}

export class FakeLiveHub {
  public subscriptions = new Map<string, FakeLiveSubscription>();
  public serverTimeOffsetMs = 0;
  public status: 'connected' | 'connecting' | 'reconnecting' | 'disconnected' = 'connected';

  subscribe<TItem, TDelta>(
    topic: string,
    filter: unknown,
    handlers: {
      onSnapshot: (items: TItem[], sequence: number, complete: boolean) => void;
      onDelta: (delta: TDelta, sequence: number) => void;
      onResync: (reason: string) => void;
    },
  ) {
    const sub: FakeLiveSubscription<unknown, unknown> = {
      topic,
      filter,
      handlers: handlers as FakeLiveSubscription['handlers'],
      lastSequence: 0,
    };
    this.subscriptions.set(topic, sub);
    return {
      update: async (f: unknown) => {
        sub.filter = f;
      },
      close: async () => {
        this.subscriptions.delete(topic);
      },
      lastSequence: sub.lastSequence,
    };
  }

  emitSnapshot<TItem>(topic: string, items: TItem[], sequence: number, complete = true) {
    const sub = this.subscriptions.get(topic);
    if (sub) {
      sub.lastSequence = sequence;
      sub.handlers.onSnapshot(items, sequence, complete);
    }
  }

  emitDelta<TDelta>(topic: string, delta: TDelta, sequence: number) {
    const sub = this.subscriptions.get(topic);
    if (sub) {
      sub.lastSequence = sequence;
      sub.handlers.onDelta(delta, sequence);
    }
  }

  emitResync(topic: string, reason = 'resync') {
    const sub = this.subscriptions.get(topic);
    if (sub) {
      sub.handlers.onResync(reason);
    }
  }
}

export function mockProblem(
  status: number,
  code: string,
  args?: Record<string, unknown>,
  fieldErrors?: Record<string, string>,
) {
  const problem: {
    status: number;
    code: string;
    messageKey: string;
    args?: Record<string, unknown>;
    fieldErrors?: Record<string, string>;
  } = {
    status,
    code,
    messageKey: `errors.${code}`,
  };
  if (args) problem.args = args;
  if (fieldErrors) problem.fieldErrors = fieldErrors;
  return problem;
}

export function expectNoUntranslatedText(container: HTMLElement): void {
  const text = container.textContent ?? '';
  const missingMatch = /⟦([^⟧]+)⟧/.exec(text);
  if (missingMatch) {
    throw new Error(`Found untranslated key marker "${missingMatch[0]}" in container: "${text}"`);
  }
}

export function renderWithProviders(ui: ReactElement, options: RenderWithProvidersOptions = {}): RenderResult {
  const {
    culture = 'en',
    theme = 'dark',
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    }),
  } = options;

  function ProvidersWrapper({ children }: { children: ReactNode }) {
    const [ready, setReady] = useState(false);

    useEffect(() => {
      void initI18n({ culture }).then(() => {
        applyTheme(theme);
        setReady(true);
      });
    }, []);

    if (!ready) {
      return null;
    }

    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  }

  return render(ui, {
    wrapper: ProvidersWrapper,
    ...options,
  });
}
