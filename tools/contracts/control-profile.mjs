// One reviewed source for Stop and MCP bootstrap bounds; never caller-controlled options.
const fields = ['capabilityId', 'schemaRevision', 'ordinaryActiveRequests', 'maximumControlRequests',
  'maximumSetupRequests', 'maximumSetupRequestBytes', 'maximumSetupResponseBytes',
  'ordinaryMessagesPerWindow', 'maximumControlMessagesPerWindow', 'windowMilliseconds',
  'maximumRequestBytes', 'maximumResponseBytes', 'maximumOutputMetadataBytes'];

export function requireControlTransportProfile(value, declarations) {
  const fail = () => { throw new Error('INVALID_CONTROL_TRANSPORT_PROFILE'); };
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || Object.keys(value).length !== fields.length || fields.some(field => !Object.hasOwn(value, field))) fail();
  if (value.capabilityId !== 'tasks.stop' || value.schemaRevision !== '1') fail();
  for (const field of fields.slice(2))
    if (!Number.isSafeInteger(value[field]) || value[field] < 1 || value[field] > 2147483647) fail();
  if (value.maximumRequestBytes > value.maximumResponseBytes
    || value.maximumSetupRequestBytes > value.maximumSetupResponseBytes || value.maximumSetupResponseBytes > 1048576
    || value.maximumResponseBytes > 1048576 || value.maximumOutputMetadataBytes >= value.maximumResponseBytes
    || value.maximumControlMessagesPerWindow < value.maximumControlRequests) fail();
  const declaration = declarations.find(item => item.id === value.capabilityId && item.schemaRevision === value.schemaRevision);
  if (!declaration || declaration.effect !== 'write' || declaration.toolName !== 'skylines_task_stop') fail();
  return Object.freeze(Object.fromEntries(fields.map(field => [field, value[field]])));
}
