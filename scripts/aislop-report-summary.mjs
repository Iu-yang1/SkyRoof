import { readFileSync, writeFileSync } from 'node:fs';

const [source, destination] = process.argv.slice(2);
if (!source || !destination) throw new Error('Usage: summary <report.json> <summary.md>');
const report = JSON.parse(readFileSync(source, 'utf8'));
const diagnostics = Array.isArray(report.diagnostics) ? report.diagnostics : [];
const bySeverity = new Map();
const byRule = new Map();
for (const d of diagnostics) {
  const severity = String(d.severity ?? d.level ?? 'unknown').toLowerCase();
  const rule = String(d.ruleId ?? d.rule ?? d.code ?? 'unknown');
  bySeverity.set(severity, (bySeverity.get(severity) ?? 0) + 1);
  byRule.set(rule, (byRule.get(rule) ?? 0) + 1);
}
const lines = [
  '# aislop full-source scan',
  '',
  '- Tool: **aislop v0.18.1** (pinned)',
  '- Score: **' + (report.score ?? '?') + '/100**',
  '- Findings: **' + diagnostics.length + '**',
  '- Status: historical findings accepted only through reviewed baseline',
  '',
  '## Severity counts',
  ...[...bySeverity].sort((a, b) => b[1] - a[1])
    .map(([name, count]) => '- ' + name + ': ' + count),
  '',
  '## Top rules',
  ...[...byRule].sort((a, b) => b[1] - a[1]).slice(0, 15)
    .map(([name, count]) => '- ' + name + ': ' + count),
  '',
  'Full details are archived as JSON. CI fails on new unbaselined findings.',
  ''
];
writeFileSync(destination, lines.join('\n'), 'utf8');
