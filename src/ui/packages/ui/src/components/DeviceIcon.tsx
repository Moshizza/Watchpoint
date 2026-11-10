import {
  Camera,
  Cpu,
  Database,
  DoorClosed,
  Gauge,
  HelpCircle,
  Network,
  Power,
  Radio,
  Server,
  Shield,
  Smartphone,
  Thermometer,
  Video,
  Wifi,
} from 'lucide-react';
import type { ElementType } from 'react';
import { cn } from '../utils.ts';

export const DEVICE_ICONS: Record<string, ElementType> = {
  camera: Camera,
  cctv: Video,
  server: Server,
  door: DoorClosed,
  sensor: Thermometer,
  gauge: Gauge,
  network: Network,
  power: Power,
  rfid: Radio,
  security: Shield,
  mobile: Smartphone,
  database: Database,
  cpu: Cpu,
  wifi: Wifi,
};

export interface DeviceIconProps {
  type: string;
  className?: string;
  'aria-label'?: string;
}

export function DeviceIcon({ type, className, 'aria-label': ariaLabel }: DeviceIconProps) {
  const normalized = type.toLowerCase().replace(/[^a-z0-9]/g, '');
  const Icon = DEVICE_ICONS[normalized] ?? HelpCircle;

  return (
    <Icon
      className={cn('w-4 h-4 shrink-0', className)}
      aria-label={ariaLabel}
      aria-hidden={ariaLabel ? undefined : 'true'}
      data-testid="device-icon"
      data-type={type}
    />
  );
}
