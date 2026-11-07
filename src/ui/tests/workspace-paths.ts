import { readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

/** The src/ui folder. */
export const workspaceRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

/** Folder names of every package under packages/. */
export const packageFolders = readdirSync(join(workspaceRoot, 'packages'), { withFileTypes: true })
  .filter((entry) => entry.isDirectory())
  .map((entry) => entry.name)
  .sort();
