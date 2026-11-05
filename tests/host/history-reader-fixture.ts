import { historyCapture, historyCaptureV2, historyCaptureV3 } from './history-values.ts';

// A bounded protocol adversary, never a file/ACL or real Native reader fixture.
const mode = process.argv[2];
let input = Buffer.alloc(0);
for await (const chunk of process.stdin) {
  if (input.length + chunk.length > 4096) { process.exitCode = 80; break; }
  input = Buffer.concat([input, chunk as Buffer]);
}
if (process.exitCode === undefined) {
  const request = JSON.parse(input.toString('utf8')) as { formatVersion: number; environmentId: string; profileId: string; query?: unknown };
  const value = request.formatVersion === 3 && mode !== 'wrong-version'
    ? { ...historyCaptureV3, environmentId: request.environmentId, profileId: request.profileId, query: request.query }
    : request.formatVersion === 2 && mode !== 'wrong-version'
    ? { ...historyCaptureV2, environmentId: request.environmentId, profileId: request.profileId, query: request.query }
    : { ...historyCapture, environmentId: request.environmentId, profileId: request.profileId };
  if (mode === 'busy') process.exitCode = 82;
  else if (mode === 'unknown') process.exitCode = 93;
  else if (mode === 'invalid') process.stdout.write('{}{}');
  else if (mode === 'utf8') process.stdout.write(Buffer.from([0xff]));
  else if (mode === 'large') process.stdout.write(Buffer.alloc(request.formatVersion === 3 ? 49153 : 32769, 32));
  else if (mode === 'eight-large' && value.formatVersion === 3) {
    const text = '\u{1F680}'.repeat(128);
    const expanded = { ...value, source: { ...value.source, throughSequence: 129, throughBytes: 2097152,
      storeId: text, producerInstanceId: text, producerVersion: '\u{1F680}'.repeat(64) },
      result: { kind: 'tasks', tasks: Array.from({ length: 8 }, (_, index) => ({
        taskId: String(index) + '\u{1F680}'.repeat(127), cityId: text, title: text,
        owner: { profileId: 'p'.repeat(64), profileInstanceId: 'd'.repeat(32), authorityEpoch: 9007199254740991, grantEpoch: 9007199254740991 },
        goalRevision: 9007199254740991, controlVersion: 9007199254740991, isStopped: false, createdSequence: index + 2, lastDecisionSequence: index + 2,
      })) } };
    // Match a Native default writer's UTF-16 escaping without running a file reader.
    process.stdout.write(JSON.stringify(expanded).replace(/[\u0080-\uFFFF]/g, character => '\\u' + character.charCodeAt(0).toString(16).padStart(4, '0')));
  }
  else {
    process.stdout.write(JSON.stringify(value));
    if (mode === 'hold') setInterval(() => {}, 1000);
    else if (mode === 'nonzero') process.exitCode = 88;
  }
}
input.fill(0);
