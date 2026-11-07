import { AST_NODE_TYPES } from '@typescript-eslint/utils';
import { createRule } from '../create-rule.ts';

const GLOBAL_OBJECTS = new Set(['window', 'globalThis', 'self']);

export const noDirectFetch = createRule<[], 'directFetch'>({
  name: 'no-direct-fetch',
  meta: {
    type: 'problem',
    docs: {
      description: 'Forbid fetch() outside @watchpoint/api-client (authentication, errors and retries live there).',
    },
    messages: { directFetch: 'Do not call fetch() directly: use @watchpoint/api-client.' },
    schema: [],
  },
  defaultOptions: [],
  create(context) {
    return {
      CallExpression(node) {
        const callee = node.callee;
        if (callee.type === AST_NODE_TYPES.Identifier && callee.name === 'fetch') {
          // Only the global fetch: a local function or import named fetch is fine.
          const variable = findVariable(context.sourceCode.getScope(node));
          if (variable === undefined || variable.defs.length === 0) {
            context.report({ node, messageId: 'directFetch' });
          }
          return;
        }
        if (
          callee.type === AST_NODE_TYPES.MemberExpression &&
          callee.object.type === AST_NODE_TYPES.Identifier &&
          GLOBAL_OBJECTS.has(callee.object.name) &&
          ((callee.property.type === AST_NODE_TYPES.Identifier &&
            !callee.computed &&
            callee.property.name === 'fetch') ||
            (callee.property.type === AST_NODE_TYPES.Literal && callee.property.value === 'fetch'))
        ) {
          context.report({ node, messageId: 'directFetch' });
        }
      },
    };
  },
});

type Scope = ReturnType<Parameters<typeof noDirectFetch.create>[0]['sourceCode']['getScope']>;

function findVariable(scope: Scope | null): ReturnType<Scope['set']['get']> {
  for (let current = scope; current !== null; current = current.upper) {
    const variable = current.set.get('fetch');
    if (variable !== undefined) {
      return variable;
    }
  }
  return undefined;
}
