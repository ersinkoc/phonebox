// PhoneBox iOS shell, injected at document start into the panel's main frame only.
//
// The panel asks the agent for data with fetch('/agent/api/…'). WebKit refuses fetch() to a custom URL scheme,
// so this shim hands those requests to the native PanelProxyHandler. Native code adds the paired computer's
// Bearer token and pins its certificate; neither is ever visible to this page. Everything else is left to the
// browser's real fetch().
(function () {
  'use strict';

  var HANDLER = 'phoneboxProxy';
  var originalFetch = window.fetch ? window.fetch.bind(window) : null;

  function bridge() {
    try {
      var handlers = window.webkit && window.webkit.messageHandlers;
      return handlers && handlers[HANDLER] ? handlers[HANDLER] : null;
    } catch (_) {
      return null;
    }
  }

  function isRequest(value) {
    return typeof Request !== 'undefined' && value instanceof Request;
  }

  // The same-origin /agent URL for a fetch input, or null for anything else (external feeds, data: URLs, …).
  function agentURL(input) {
    try {
      var raw = typeof input === 'string' ? input : (input && input.url);
      if (typeof raw !== 'string') return null;
      var url = new URL(raw, window.location.href);
      if (url.protocol !== window.location.protocol || url.host !== window.location.host) return null;
      if (url.pathname.indexOf('/agent/') !== 0) return null;
      return url;
    } catch (_) {
      return null;
    }
  }

  function toBase64(bytes) {
    var chunks = '';
    var step = 0x8000;
    for (var i = 0; i < bytes.length; i += step) {
      chunks += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
    }
    return btoa(chunks);
  }

  function fromBase64(text) {
    var binary = atob(text || '');
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
  }

  async function bodyBytes(payload) {
    if (typeof payload === 'string') return new TextEncoder().encode(payload);
    if (payload instanceof ArrayBuffer) return new Uint8Array(payload);
    if (ArrayBuffer.isView(payload)) return new Uint8Array(payload.buffer, payload.byteOffset, payload.byteLength);
    if (payload && typeof payload.arrayBuffer === 'function') return new Uint8Array(await payload.arrayBuffer());
    throw new TypeError('unsupported request body');
  }

  function postWithSignal(handler, message, signal) {
    if (!signal) return handler.postMessage(message);
    return new Promise(function (resolve, reject) {
      function onAbort() {
        reject(signal.reason !== undefined ? signal.reason : new DOMException('Aborted', 'AbortError'));
      }
      if (signal.aborted) { onAbort(); return; }
      signal.addEventListener('abort', onAbort, { once: true });
      try {
        Promise.resolve(handler.postMessage(message)).then(resolve, reject).finally(function () {
          signal.removeEventListener('abort', onAbort);
        });
      } catch (error) {
        signal.removeEventListener('abort', onAbort);
        reject(error);
      }
    });
  }

  async function proxy(input, init) {
    var handler = bridge();
    if (!handler) throw new TypeError('PhoneBox bridge unavailable');

    var url = agentURL(input);
    if (!url) throw new TypeError('PhoneBox bridge: not an agent request');

    var method = String((init && init.method) || (isRequest(input) ? input.method : 'GET')).toUpperCase();
    var headerSource = (init && init.headers) ? init.headers : (isRequest(input) ? input.headers : undefined);
    var headers = {};
    new Headers(headerSource).forEach(function (value, key) { headers[key] = value; });

    var body = '';
    if (method !== 'GET' && method !== 'HEAD') {
      var payload = (init && init.body !== undefined && init.body !== null)
        ? init.body
        : (isRequest(input) ? await input.clone().arrayBuffer() : null);
      if (payload !== null && payload !== undefined) body = toBase64(await bodyBytes(payload));
    }

    var signal = (init && init.signal) || (isRequest(input) ? input.signal : null);
    if (signal && signal.aborted) throw new DOMException('Aborted', 'AbortError');

    var reply = await postWithSignal(handler, {
      key: window.__phoneboxBridgeKey || '',
      method: method,
      path: url.pathname + url.search,
      headers: headers,
      body: body
    }, signal);

    if (signal && signal.aborted) throw new DOMException('Aborted', 'AbortError');
    if (!reply) throw new TypeError('PhoneBox bridge failed');
    if (reply.error) throw new TypeError(String(reply.error));

    var status = Number(reply.status) || 0;
    if (status < 200 || status > 599) status = 502;
    var responseBody = status === 204 || status === 304 ? null : fromBase64(reply.body || '');
    return new Response(responseBody, {
      status: status,
      statusText: reply.statusText || '',
      headers: reply.headers || {}
    });
  }

  if (!originalFetch) return;

  window.fetch = function (input, init) {
    if (!agentURL(input)) return originalFetch.call(window, input, init);
    return proxy(input, init);
  };
})();
