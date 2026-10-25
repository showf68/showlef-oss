(async () => {
  const r = await new Promise(res => chrome.developerPrivate.getExtensionInfo('mepcfiemlmpffknhaebjocpddbaahdfc', res));
  return JSON.stringify({ name: r.name, state: r.state, runtimeErrors: (r.runtimeErrors || []).map(e => e.message), manifestErrors: (r.manifestErrors || []).map(e => e.message), warnings: r.installWarnings });
})()
