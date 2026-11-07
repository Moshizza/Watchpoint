import { AST_NODE_TYPES, type TSESTree } from '@typescript-eslint/utils';
import { createRule } from '../create-rule.ts';

/** Physical Tailwind utility prefixes and their logical replacement (so right-to-left layouts mirror). */
const PREFIXES: ReadonlyArray<readonly [string, string]> = [
  ['scroll-ml', 'scroll-ms'],
  ['scroll-mr', 'scroll-me'],
  ['scroll-pl', 'scroll-ps'],
  ['scroll-pr', 'scroll-pe'],
  ['ml', 'ms'],
  ['mr', 'me'],
  ['pl', 'ps'],
  ['pr', 'pe'],
  ['left', 'start'],
  ['right', 'end'],
  ['border-l', 'border-s'],
  ['border-r', 'border-e'],
  ['rounded-tl', 'rounded-ss'],
  ['rounded-tr', 'rounded-se'],
  ['rounded-bl', 'rounded-es'],
  ['rounded-br', 'rounded-ee'],
  ['rounded-l', 'rounded-s'],
  ['rounded-r', 'rounded-e'],
];

/** Whole physical utilities and their logical replacement. */
const EXACT = new Map<string, string>([
  ['text-left', 'text-start'],
  ['text-right', 'text-end'],
  ['float-left', 'float-start'],
  ['float-right', 'float-end'],
  ['clear-left', 'clear-start'],
  ['clear-right', 'clear-end'],
  ['border-l', 'border-s'],
  ['border-r', 'border-e'],
  ['rounded-l', 'rounded-s'],
  ['rounded-r', 'rounded-e'],
]);

/** Functions whose string arguments are class lists. */
const CLASS_FUNCTIONS = new Set(['cn', 'clsx', 'cx', 'cva', 'twMerge', 'twJoin', 'classNames']);

/** Returns the logical replacement of a physical utility, or undefined when the class is fine. */
export function logicalReplacement(className: string): string | undefined {
  // Strip variants (md:, hover:, rtl:…), the important marker and a negative sign.
  const utility = className
    .slice(className.lastIndexOf(':') + 1)
    .replace(/^!/, '')
    .replace(/!$/, '');
  const negative = utility.startsWith('-');
  const bare = negative ? utility.slice(1) : utility;
  const exact = EXACT.get(bare);
  if (exact !== undefined) {
    return (negative ? '-' : '') + exact;
  }
  for (const [physical, logical] of PREFIXES) {
    if (bare.startsWith(`${physical}-`)) {
      return (negative ? '-' : '') + logical + bare.slice(physical.length);
    }
  }
  return undefined;
}

export const noPhysicalDirectionClasses = createRule<[], 'physical'>({
  name: 'no-physical-direction-classes',
  meta: {
    type: 'problem',
    docs: {
      description: 'Forbid physical Tailwind direction utilities (ml-, pr-, left-, text-right…); use logical ones.',
    },
    messages: { physical: 'Physical class "{{className}}" breaks right-to-left layouts: use "{{replacement}}".' },
    schema: [],
  },
  defaultOptions: [],
  create(context) {
    const check = (node: TSESTree.Node, text: string): void => {
      for (const className of text.split(/\s+/)) {
        const replacement = className === '' ? undefined : logicalReplacement(className);
        if (replacement !== undefined) {
          context.report({ node, messageId: 'physical', data: { className, replacement } });
        }
      }
    };

    const checkStrings = (node: TSESTree.Node | null | undefined): void => {
      if (node == null) {
        return;
      }
      switch (node.type) {
        case AST_NODE_TYPES.Literal:
          if (typeof node.value === 'string') {
            check(node, node.value);
          }
          break;
        case AST_NODE_TYPES.TemplateLiteral:
          node.quasis.forEach((quasi) => check(quasi, quasi.value.cooked ?? ''));
          node.expressions.forEach(checkStrings);
          break;
        case AST_NODE_TYPES.JSXExpressionContainer:
          if (node.expression.type !== AST_NODE_TYPES.JSXEmptyExpression) {
            checkStrings(node.expression);
          }
          break;
        case AST_NODE_TYPES.ConditionalExpression:
          checkStrings(node.consequent);
          checkStrings(node.alternate);
          break;
        case AST_NODE_TYPES.LogicalExpression:
          checkStrings(node.right);
          break;
        case AST_NODE_TYPES.ArrayExpression:
          node.elements.forEach((element) => {
            checkStrings(element);
          });
          break;
        case AST_NODE_TYPES.ObjectExpression:
          node.properties.forEach((property) => {
            if (property.type === AST_NODE_TYPES.Property) {
              // clsx({ 'ml-2': condition }) and cva variants: keys and values can both hold classes.
              checkStrings(property.key);
              checkStrings(property.value);
            }
          });
          break;
        default:
          break;
      }
    };

    return {
      JSXAttribute(node) {
        if (
          node.name.type === AST_NODE_TYPES.JSXIdentifier &&
          (node.name.name === 'className' || node.name.name === 'class')
        ) {
          checkStrings(node.value);
        }
      },
      CallExpression(node) {
        if (node.callee.type === AST_NODE_TYPES.Identifier && CLASS_FUNCTIONS.has(node.callee.name)) {
          node.arguments.forEach(checkStrings);
        }
      },
    };
  },
});
