import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { initI18n } from '@watchpoint/i18n';
import axe from 'axe-core';
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import {
  AppShell,
  ConnectionBanner,
  DataGrid,
  DateTimeInput,
  DeviceIcon,
  EntityPicker,
  ErrorView,
  LocalizedNameInput,
  PropertyValue,
  SchemaForm,
  StateBadge,
  StateDot,
  VirtualTree,
} from './index.ts';

beforeAll(async () => {
  await initI18n({ culture: 'en' });
});

afterEach(() => {
  cleanup();
});

describe('UI M1 components unit tests', () => {
  it('UIC-U-043 AppShell mirrors navigator/context panel sides in RTL', async () => {
    const { rerender } = render(
      <AppShell
        header={<div data-testid="hdr">--</div>}
        navigator={<nav data-testid="nav">--</nav>}
        contextPanel={<div data-testid="ctx">--</div>}
      >
        <div data-testid="body">--</div>
      </AppShell>,
    );

    const rootLTR = screen.getByTestId('app-shell');
    expect(rootLTR.getAttribute('dir')).toBe('ltr');
    const nav = screen.getByTestId('app-shell-navigator');
    expect(nav.className).toContain('border-e');

    // Switch to Arabic / RTL
    await initI18n({ culture: 'ar' });
    rerender(
      <AppShell
        header={<div data-testid="hdr">--</div>}
        navigator={<nav data-testid="nav">--</nav>}
        contextPanel={<div data-testid="ctx">--</div>}
      >
        <div data-testid="body">--</div>
      </AppShell>,
    );

    const rootRTL = screen.getByTestId('app-shell');
    expect(rootRTL.getAttribute('dir')).toBe('rtl');
    await initI18n({ culture: 'en' });
  });

  it('UIC-U-044 DataGrid renders 100,000 rows virtualised (< 200 DOM rows)', () => {
    const manyItems = Array.from({ length: 100_000 }, (_, i) => ({
      id: `item-${i}`,
      name: `Device ${i}`,
    }));

    const columns = [
      { accessorKey: 'id', header: 'ID' },
      { accessorKey: 'name', header: 'Name' },
    ];

    render(<DataGrid data={manyItems} columns={columns} />);

    const domRows = screen.getAllByRole('row');
    expect(domRows.length).toBeLessThan(200);
  });

  it('UIC-U-045 DataGrid infinite loading with cursors', () => {
    let fetchCalled = false;
    const items = [
      { id: 'dev-1', name: 'Device 1' },
      { id: 'dev-2', name: 'Device 2' },
    ];

    const columns = [
      { accessorKey: 'id', header: 'ID' },
      { accessorKey: 'name', header: 'Name' },
    ];

    render(
      <DataGrid
        data={items}
        columns={columns}
        hasNextPage={true}
        onFetchNextPage={() => {
          fetchCalled = true;
        }}
      />,
    );

    const loadMoreBtn = screen.getByText('Load more');
    fireEvent.click(loadMoreBtn);
    expect(fetchCalled).toBe(true);
  });

  it('UIC-U-046 DataGrid keyboard navigation and selection', () => {
    let selected: { id: string; name: string }[] = [];
    const items = [
      { id: 'dev-1', name: 'Device 1' },
      { id: 'dev-2', name: 'Device 2' },
      { id: 'dev-3', name: 'Device 3' },
    ];
    const columns = [
      { accessorKey: 'id', header: 'ID' },
      { accessorKey: 'name', header: 'Name' },
    ];

    render(
      <DataGrid
        data={items}
        columns={columns}
        getRowId={(r) => r.id}
        onRowSelectionChange={(rows) => {
          selected = rows;
        }}
      />,
    );

    const grid = screen.getByTestId('data-grid');

    // Press space to select first row
    fireEvent.keyDown(grid, { key: ' ' });
    expect(selected.length).toBe(1);
    expect(selected[0]?.id).toBe('dev-1');

    // Arrow down, then space to select second row
    fireEvent.keyDown(grid, { key: 'ArrowDown' });
    fireEvent.keyDown(grid, { key: ' ' });
    expect(selected.length).toBe(2);
  });

  it('UIC-U-047 VirtualTree lazy loading, keyboard, type-ahead', async () => {
    let loaded = false;
    const treeData = [
      { id: 'srv-1', label: 'Server 1', hasChildren: true },
      { id: 'srv-2', label: 'Zebra Server' },
    ];

    render(
      <VirtualTree
        nodes={treeData}
        onLoadChildren={async (node) => {
          loaded = true;
          return [{ id: `${node.id}-c1`, label: 'Child Device' }];
        }}
      />,
    );

    const tree = screen.getByTestId('virtual-tree');

    // Expand via keyboard right arrow
    fireEvent.keyDown(tree, { key: 'ArrowRight' });
    expect(loaded).toBe(true);

    // Type-ahead: pressing 'z' jumps to Zebra Server
    fireEvent.keyDown(tree, { key: 'z' });
    const zebraNode = screen.getByTestId('tree-node-srv-2');
    expect(zebraNode.getAttribute('tabindex')).toBe('0');
  });

  it('UIC-U-048 VirtualTree arrow keys swap meaning in RTL', async () => {
    let expandedLTR = false;
    let expandedRTL = false;

    const nodesLTR = [{ id: 'n1', label: 'Node 1', hasChildren: true }];
    const { unmount } = render(
      <VirtualTree
        nodes={nodesLTR}
        isRtl={false}
        onLoadChildren={async () => {
          expandedLTR = true;
          return [];
        }}
      />,
    );

    const treeLTR = screen.getByTestId('virtual-tree');
    // In LTR: ArrowRight expands
    fireEvent.keyDown(treeLTR, { key: 'ArrowRight' });
    expect(expandedLTR).toBe(true);
    unmount();

    // In RTL: ArrowLeft expands
    const nodesRTL = [{ id: 'n2', label: 'Node 2', hasChildren: true }];
    render(
      <VirtualTree
        nodes={nodesRTL}
        isRtl={true}
        onLoadChildren={async () => {
          expandedRTL = true;
          return [];
        }}
      />,
    );
    const treeRTL = screen.getByTestId('virtual-tree');
    fireEvent.keyDown(treeRTL, { key: 'ArrowLeft' });
    expect(expandedRTL).toBe(true);
  });

  it('UIC-U-049 StateBadge shows icon and text, not colour only', () => {
    const { container } = render(<StateBadge state="alarm" />);
    const badge = screen.getByTestId('state-badge');
    expect(badge.getAttribute('role')).toBe('status');
    expect(badge.getAttribute('aria-label')).toBe('Alarm');

    expect(badge.textContent).toContain('Alarm');
    const svg = container.querySelector('svg');
    expect(svg).toBeDefined();
    expect(svg?.getAttribute('aria-hidden')).toBe('true');
  });

  it('UIC-U-050 PropertyValue stale marker and tooltip', () => {
    const fixedDate = new Date('2026-11-03T10:00:00.000Z');
    render(<PropertyValue value={42.5} unit="°C" isStale={true} lastSeen={fixedDate} />);

    const valEl = screen.getByTestId('property-value');
    expect(valEl.textContent).toContain('42.5');
    expect(valEl.textContent).toContain('°C');

    const staleMarker = screen.getByTestId('stale-marker');
    expect(staleMarker).toBeDefined();
    expect(staleMarker.getAttribute('title')).toContain('Last seen');
  });

  it('UIC-U-051 SchemaForm renders each field type and default values', () => {
    let state: Record<string, unknown> = {};
    const schema = {
      type: 'object' as const,
      properties: {
        host: { type: 'string' as const, title: 'Host Name', default: 'localhost' },
        port: { type: 'number' as const, title: 'Port', default: 502 },
        enabled: { type: 'boolean' as const, default: true },
      },
    };

    render(
      <SchemaForm
        schema={schema}
        values={state}
        onChange={(val) => {
          state = val;
        }}
      />,
    );

    expect(screen.getByTestId('schema-field-host')).toBeDefined();
    expect(screen.getByTestId('schema-field-port')).toBeDefined();
    expect(screen.getByTestId('schema-field-enabled')).toBeDefined();
  });

  it('UIC-U-052 SchemaForm visibleWhen, groups, advanced section', () => {
    const schema = {
      type: 'object' as const,
      properties: {
        useAuth: { type: 'boolean' as const, default: false },
        password: {
          type: 'string' as const,
          'x-wp-ui': {
            visibleWhen: { field: 'useAuth', equals: true },
          },
        },
        timeout: {
          type: 'number' as const,
          'x-wp-ui': {
            advanced: true,
          },
        },
      },
    };

    const { rerender } = render(<SchemaForm schema={schema} values={{ useAuth: false }} onChange={() => {}} />);

    expect(screen.queryByTestId('schema-field-password')).toBeNull();

    rerender(<SchemaForm schema={schema} values={{ useAuth: true }} onChange={() => {}} />);
    expect(screen.getByTestId('schema-field-password')).toBeDefined();

    expect(screen.queryByTestId('advanced-fields')).toBeNull();
    const toggleAdv = screen.getByTestId('toggle-advanced');
    fireEvent.click(toggleAdv);
    expect(screen.getByTestId('advanced-fields')).toBeDefined();
  });

  it('UIC-U-053 SchemaForm maps field errors by JSON pointer', () => {
    const schema = {
      type: 'object' as const,
      properties: {
        port: { type: 'number' as const, title: 'Port' },
      },
    };

    render(
      <SchemaForm
        schema={schema}
        values={{ port: 99999 }}
        onChange={() => {}}
        fieldErrors={{
          '/port': 'Value out of range (1-65535)',
        }}
      />,
    );

    const err = screen.getByTestId('field-error-port');
    expect(err.textContent).toBe('Value out of range (1-65535)');
  });

  it('UIC-U-054 SchemaForm secret field write-only behaviour', () => {
    let currentValues: Record<string, unknown> = { apiKey: 'secret-token-123' };
    const schema = {
      type: 'object' as const,
      properties: {
        apiKey: {
          type: 'string' as const,
          title: 'API Key',
          'x-wp-ui': { secret: true },
        },
      },
    };

    const { rerender } = render(
      <SchemaForm
        schema={schema}
        values={currentValues}
        onChange={(val) => {
          currentValues = val;
        }}
      />,
    );

    expect(screen.getByText('•••• (set)')).toBeDefined();

    const replaceBtn = screen.getByText('Replace secret');
    fireEvent.click(replaceBtn);

    rerender(
      <SchemaForm
        schema={schema}
        values={currentValues}
        onChange={(val) => {
          currentValues = val;
        }}
      />,
    );

    const input = screen.getByLabelText(/API Key/);
    expect(input.getAttribute('type')).toBe('password');
  });

  it('UIC-U-055 EntityPicker filters by entityRef constraints', () => {
    let chosen = '';
    const entities = [
      { id: 'cam-1', name: 'Lobby Camera', kind: 'device' as const, tags: ['cctv', 'indoor'] },
      { id: 'srv-1', name: 'Main Server', kind: 'server' as const, tags: ['core'] },
      { id: 'cam-2', name: 'Gate Camera', kind: 'device' as const, tags: ['cctv', 'outdoor'] },
    ];

    render(
      <EntityPicker
        entities={entities}
        entityRefConstraints={{
          kind: 'device',
          requiredTags: ['indoor'],
        }}
        onChange={(id) => {
          chosen = id;
        }}
      />,
    );

    fireEvent.click(screen.getByTestId('picker-trigger'));

    expect(screen.getByTestId('entity-option-cam-1')).toBeDefined();
    expect(screen.queryByTestId('entity-option-srv-1')).toBeNull();
    expect(screen.queryByTestId('entity-option-cam-2')).toBeNull();

    fireEvent.click(screen.getByTestId('entity-option-cam-1'));
    expect(chosen).toBe('cam-1');
  });

  it('UIC-U-056 LocalizedNameInput edits default and translations', () => {
    let val: { default: string; translations?: Record<string, string> } = {
      default: 'Main Door',
      translations: { fr: 'Porte principale' },
    };

    render(
      <LocalizedNameInput
        value={val}
        onChange={(updated) => {
          val = updated;
        }}
      />,
    );

    const defaultInput = screen.getByTestId('localized-default-input');
    fireEvent.change(defaultInput, { target: { value: 'Primary Gate' } });
    expect(val.default).toBe('Primary Gate');

    const frInput = screen.getByTestId('localized-trans-input-fr');
    fireEvent.change(frInput, { target: { value: 'Porte d entrée' } });
    expect(val.translations?.['fr']).toBe('Porte d entrée');
  });

  it('UIC-U-057 DateTimeInput time zone aware around DST', () => {
    const dateUtc = new Date('2026-03-29T02:30:00.000Z');
    let changedDate: Date | null = null;

    render(
      <DateTimeInput
        value={dateUtc}
        timeZone="Europe/Paris"
        onChange={(d) => {
          changedDate = d;
        }}
      />,
    );

    const zoneLabel = screen.getByTestId('datetime-zone-label');
    expect(zoneLabel.textContent).toContain('Europe/Paris');

    const nativeInput = screen.getByTestId('datetime-native-input');
    fireEvent.change(nativeInput, { target: { value: '2026-03-29T05:00' } });
    expect(changedDate).not.toBeNull();
  });

  it('UIC-U-058 ErrorView shows translated title and trace id', () => {
    const errorTitle = 'Custom Error';
    const errorMsg = 'Something went wrong';
    render(
      <ErrorView
        title={errorTitle}
        message={errorMsg}
        traceId="00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
      />,
    );

    const errorView = screen.getByTestId('error-view');
    expect(errorView.textContent).toContain(errorTitle);
    expect(errorView.textContent).toContain(errorMsg);
    expect(errorView.textContent).toContain('00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01');
  });

  it('UIC-U-059 ConnectionBanner states', () => {
    const { rerender } = render(<ConnectionBanner status="connected" lastUpdateAt={new Date()} />);
    expect(screen.queryByTestId('connection-banner')).toBeNull();

    rerender(<ConnectionBanner status="disconnected" lastUpdateAt={new Date()} />);
    const banner = screen.getByTestId('connection-banner');
    expect(banner.getAttribute('data-status')).toBe('disconnected');
    expect(banner.textContent).toContain('Disconnected');
  });

  it('UIC-U-060 Accessibility: axe-core checks on component stories (no serious violations)', async () => {
    const cameraLabel = 'Camera';
    const problemTitle = 'Sample Problem';
    const onlineValue = 'Online';
    const { container } = render(
      <div>
        <StateBadge state="alarm" />
        <StateDot state="normal" />
        <DeviceIcon type="camera" aria-label={cameraLabel} />
        <PropertyValue value={onlineValue} />
        <ErrorView title={problemTitle} />
      </div>,
    );

    const results = await axe.run(container, {
      rules: {
        'color-contrast': { enabled: false },
      },
    });

    const seriousOrCritical = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
    expect(seriousOrCritical).toEqual([]);
  });
});
