/*
 * site.js: the browser side of Static Site Host.
 *
 * Every hosted site answers this file at /_host/site.js. The host puts one line in front of it
 * for the site that asked,
 *
 *   var __siteHost = {"domain":"…","vars":{…},"version":"…","ai":{"enabled":false},
 *                     "realtime":{"path":"/_host/realtime","script":"/_host/signalr.js?v=…"}};
 *
 * and serves the result no-cache with an ETag, so a page that includes it always has current
 * values and pays for a 304 when nothing changed. Include it before the scripts that use it:
 *
 *   <script src="/_host/site.js"></script>
 *   <script>document.title = site.get('SITE_TITLE', document.title);</script>
 *
 * Plain ES5 in one function, with no build step, so it runs wherever the site's own pages do.
 * It defines one global, window.site, frozen so that nothing else on the page can rewrite it.
 * Variables need nothing more. site.realtime needs Promise, and loads the SignalR client from
 * /_host/signalr.js the first time a page uses it. It talks over WebSockets, and where the
 * browser or a proxy on the way does not allow them, falls back to what SignalR supports:
 * server-sent events, then long polling. site.ai.chat needs fetch and Promise, and reads an
 * answer as it streams in with ReadableStream and TextDecoder, which every current browser has;
 * without those two it waits for the whole answer, and onText gets it in one piece.
 */
(function (window) {
  'use strict';

  var host = window.__siteHost || {};
  var hasOwn = Object.prototype.hasOwnProperty;
  var freeze = Object.freeze || function (value) { return value; };

  /** An Error carrying the HTTP status behind it, or 0 when there was no answer. */
  function failure(message, status) {
    var error = new Error(message);
    error.status = status;
    return error;
  }

  // ---- variables ------------------------------------------------------------
  //
  // The site's public variables, all strings. Secrets and private variables never reach this
  // file: functions read those on the server.

  var vars = {};
  var source = host.vars || {};
  for (var name in source) {
    if (hasOwn.call(source, name)) vars[name] = String(source[name]);
  }
  freeze(vars);

  /** A public variable's value, or fallback when it is missing or empty. */
  function get(name, fallback) {
    return hasOwn.call(vars, name) && vars[name] !== '' ? vars[name] : fallback;
  }

  // ---- realtime -------------------------------------------------------------
  //
  // Events from the site's server side to the pages open on it, over SignalR at /_host/realtime.
  // A page listens with on(), and may join groups to hear what is sent to them as well; it can do
  // nothing else, since everything that decides who hears what happens on the server. The first
  // on(), join() or connect() opens the connection, loading the SignalR client the first time. A
  // dropped connection is tried again at once, then after 2, 5, 10 and 30 seconds, and the groups
  // the page joined are joined again when it is back; if the last try fails too, it stays
  // disconnected until the page connects again. A connection the server closes on purpose,
  // because the site was deleted or given a new passcode, is not tried again.

  var realtimeSettings = host.realtime || {};
  var hubPath = String(realtimeSettings.path || '/_host/realtime');
  var clientPath = String(realtimeSettings.script || '/_host/signalr.js');

  var connection = null;               // the HubConnection, made by the first connect and kept
  var state = 'disconnected';
  var starting = null;                 // the Promise of the connect in progress
  var stopping = null;                 // the Promise of the disconnect in progress
  var attempt = 0;                     // moved on by disconnect(), so a connect it overtook gives up
  var loadingClient = null;            // the Promise of /_host/signalr.js
  var listeners = Object.create(null); // event name -> handlers, in the order they were added
  var stateHandlers = [];
  var joined = [];                     // the groups the page is in, to join again after a reconnect
  var intents = Object.create(null);   // group -> the last join() or leave() of it, so the later one wins
  var intentCount = 0;

  /** Tells the console about a failure that nothing else will hear of. */
  function report(message, error) {
    if (window.console && typeof console.error === 'function') console.error('site.realtime: ' + message, error);
  }

  function removeFrom(list, item) {
    for (var i = 0; i < list.length; i++) {
      if (list[i] === item) {
        list.splice(i, 1);
        return;
      }
    }
  }

  /**
   * Any failure as an Error with a status: the HTTP status that refused the connection (401 on a
   * private site the visitor has not unlocked, 403 from a page on another site, 503 when the site
   * has as many connections as it allows), or 0. The message is the server's own sentence where
   * there is one: SignalR wraps the { "error": "…" } of a refused request, and a refusal from the
   * hub, in words of its own.
   */
  function realtimeFailure(error) {
    if (error && typeof error.status === 'number') return error;

    var message = String((error && error.message) || error || 'The realtime connection failed.')
      .replace(/^An unexpected error occurred invoking '[^']*' on the server\. HubException: /, '');

    // A refused negotiation keeps its status only in the words: "…: Status code '401'".
    var code = /Status code '(\d{3})'/.exec(message);
    var status = error && typeof error.statusCode === 'number' ? error.statusCode : code ? Number(code[1]) : 0;

    var refusal = /\{"error":.*\}/.exec(message);
    if (refusal) {
      try { message = String(JSON.parse(refusal[0]).error || message); } catch (notJson) { /* SignalR's words will do */ }
    }

    var wrapped = failure(message, status);
    wrapped.cause = error;
    return wrapped;
  }

  function setState(next) {
    if (next === state) return;

    var previous = state;
    state = next;

    var handlers = stateHandlers.slice();
    for (var i = 0; i < handlers.length; i++) {
      try { handlers[i](next, previous); } catch (error) { report('an onStateChange handler threw.', error); }
    }
  }

  /**
   * Calls handler(state, previous) whenever the state changes: 'disconnected', 'connecting',
   * 'connected' or 'reconnecting'. Returns a function that stops it.
   */
  function onStateChange(handler) {
    if (typeof handler !== 'function') throw new TypeError('site.realtime.onStateChange needs a function to call.');

    stateHandlers.push(handler);
    var active = true;
    return function () {
      if (!active) return;
      active = false;
      removeFrom(stateHandlers, handler);
    };
  }

  /** window.signalR, loading /_host/signalr.js for it the first time. */
  function loadClient() {
    if (window.signalR && window.signalR.HubConnectionBuilder) return Promise.resolve(window.signalR);
    if (loadingClient) return loadingClient;

    loadingClient = new Promise(function (resolve, reject) {
      var script = document.createElement('script');

      function fail(message) {
        loadingClient = null; // the next connect() tries again
        if (script.parentNode) script.parentNode.removeChild(script);
        reject(failure(message, 0));
      }

      script.src = clientPath;
      script.async = true;
      script.onload = function () {
        if (window.signalR && window.signalR.HubConnectionBuilder) resolve(window.signalR);
        else fail('The realtime client loaded but did not define window.signalR. A page with an AMD loader such as ' +
          'RequireJS takes it over; load ' + clientPath + ' before the loader instead.');
      };
      script.onerror = function () {
        fail('The realtime client could not be loaded from ' + clientPath + '.');
      };
      (document.head || document.documentElement).appendChild(script);
    });

    return loadingClient;
  }

  /** Hands an event from the server to the page's handlers for it, each on its own. */
  function dispatch(name, payload, group) {
    var handlers = listeners[name];
    if (!handlers) return;

    var details = freeze({ event: name, group: group == null ? null : group });

    // A copy, so a handler that adds or removes one does not upset this round.
    handlers = handlers.slice();
    for (var i = 0; i < handlers.length; i++) {
      try {
        handlers[i](payload, details);
      } catch (error) {
        report('a handler for "' + name + '" threw.', error);
      }
    }
  }

  /** Joins the page's groups again on a new connection. A refusal is reported, not thrown. */
  function rejoin() {
    var groups = joined.slice();
    var pending = [];

    for (var i = 0; i < groups.length; i++) {
      pending.push(connection.invoke('Join', groups[i]).then(null, (function (group) {
        return function (error) { report('could not join "' + group + '" again.', realtimeFailure(error)); };
      })(groups[i])));
    }

    return Promise.all(pending).then(function () {});
  }

  function build(signalR) {
    var built = new signalR.HubConnectionBuilder()
      .withUrl(hubPath)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    // Every event arrives as one method, with its name, its payload and the group it went to.
    built.on('event', dispatch);
    built.onreconnecting(function () { setState('reconnecting'); });
    built.onreconnected(function () {
      setState('connected');
      rejoin();
    });
    built.onclose(function () { setState('disconnected'); });
    return built;
  }

  /** Resolves when a reconnect in progress succeeds, and rejects when it gives up. */
  function untilConnected() {
    return new Promise(function (resolve, reject) {
      var stop = onStateChange(function (now) {
        if (now === 'connected') {
          stop();
          resolve();
        } else if (now === 'disconnected') {
          stop();
          reject(failure('The connection to the server was lost, and could not be made again.', 0));
        }
      });
    });
  }

  /**
   * Opens the connection, unless it is open or opening already, and resolves once it is connected
   * and back in the groups the page joined. Rejects with an Error whose status is the HTTP status
   * that refused it, or 0.
   */
  function connect() {
    if (stopping) return stopping.then(connect);
    if (state === 'connected') return Promise.resolve();
    if (starting) return starting;
    if (state === 'reconnecting') return untilConnected();

    var mine = ++attempt;

    // disconnect() was called since: it has the last word. Named like a stopped fetch, so a
    // caller can tell it from a failure.
    function overtaken() {
      var error = failure('site.realtime.disconnect() was called before the connection was made.', 0);
      error.name = 'AbortError';
      return error;
    }

    setState('connecting');
    starting = loadClient().then(function (signalR) {
      if (mine !== attempt) throw overtaken();
      connection = connection || build(signalR);
      return connection.start();
    }).then(function () {
      if (mine !== attempt) throw overtaken();
      starting = null;
      setState('connected');
      return rejoin();
    }).then(null, function (error) {
      if (mine !== attempt) throw overtaken();
      starting = null;
      setState('disconnected');
      throw realtimeFailure(error);
    });

    return starting;
  }

  /**
   * Closes the connection and forgets the groups the page joined; the handlers stay. The state is
   * 'disconnected' at once, and the Promise resolves once the connection has closed.
   */
  function disconnect() {
    function stopped() { stopping = null; }

    attempt++;
    starting = null;
    joined = [];
    intents = Object.create(null);
    setState('disconnected');

    if (!stopping && connection) stopping = connection.stop().then(stopped, stopped);
    return stopping || Promise.resolve();
  }

  /**
   * Calls handler(payload, { event, group }) for every event of that name, where group is the
   * group it was sent to, or null. Connects if the page is not connected. Returns a function that
   * removes the handler again.
   */
  function on(name, handler) {
    if (typeof handler !== 'function') throw new TypeError('site.realtime.on needs a function to call.');

    name = String(name);
    (listeners[name] || (listeners[name] = [])).push(handler);

    if (state === 'disconnected') {
      connect().then(null, function (error) {
        if (error.name !== 'AbortError') report('could not connect.', error);
      });
    }

    var active = true;
    return function () {
      if (!active) return;
      active = false;
      off(name, handler);
    };
  }

  /** Removes a handler on() added; without a handler, every handler for that event. */
  function off(name, handler) {
    name = String(name);

    var handlers = listeners[name];
    if (!handlers) return;

    if (handler === undefined) handlers.length = 0;
    else removeFrom(handlers, handler);

    if (!handlers.length) delete listeners[name];
  }

  /**
   * Puts the page in a group, so it also receives what is sent to that group; connects first if
   * it has to. Resolves once the page is in it, and from then on it is joined again after every
   * reconnect. Rejects with the server's reason when the name is not a group name (letters,
   * digits, '_', '.', ':' and '-', up to 64 of them) or the page or the site has as many groups as
   * it may.
   */
  function join(group) {
    group = String(group);
    var mine = intents[group] = ++intentCount;

    return connect().then(function () {
      return connection.invoke('Join', group);
    }).then(function () {
      // A leave() or disconnect() since then has the last word.
      if (intents[group] === mine && joined.indexOf(group) < 0) joined.push(group);
    }, function (error) {
      throw realtimeFailure(error);
    });
  }

  /**
   * Takes the page out of a group, and stops joining it again after a reconnect. A page that is
   * not connected is in no groups, so then there is nothing to send and it resolves at once.
   */
  function leave(group) {
    group = String(group);
    intents[group] = ++intentCount;
    removeFrom(joined, group);

    if (state !== 'connected') return Promise.resolve();

    return connection.invoke('Leave', group).then(function () {}, function (error) {
      throw realtimeFailure(error);
    });
  }

  var realtime = freeze({
    /** 'disconnected', 'connecting', 'connected' or 'reconnecting'. */
    get state() { return state; },

    /**
     * This page's connection id while it is connected, or null. A function can reach this page
     * alone with it, and it changes with every reconnect.
     */
    get connectionId() { return state === 'connected' && connection ? connection.connectionId || null : null; },

    connect: connect,
    disconnect: disconnect,
    on: on,
    off: off,
    join: join,
    leave: leave,
    onStateChange: onStateChange
  });

  // ---- ai -------------------------------------------------------------------
  //
  // Chat through the site's AI provider at /_host/ai/chat. The provider, its key, the model and
  // the site's system prompt stay on the server: a page sends the conversation and, if it likes,
  // instructions of its own, which follow the site's. enabled is true when the site has a
  // provider and either lets every visitor use it or has an [AiAccess] function to decide who may,
  // which it does per request, so a visitor it turns away sees enabled too. The server has the last
  // word either way, and a refusal rejects with the status it answered: 403 for a visitor who may not.

  var aiSettings = host.ai || {};
  var chatPath = '/_host/ai/chat';

  /** Any rejection as a failure with a status. A stopped chat keeps the name AbortError. */
  function asFailure(error) {
    if (error && typeof error.status === 'number') return error;

    var aborted = !!error && error.name === 'AbortError';
    var wrapped = failure(aborted ? 'The chat was stopped.' : 'The chat failed: ' + ((error && error.message) || error), 0);
    if (aborted) wrapped.name = 'AbortError';
    wrapped.cause = error;
    return wrapped;
  }

  function answer(text, details) {
    return {
      text: text,
      model: details.model,
      inputTokens: details.inputTokens,
      outputTokens: details.outputTokens,
      stopReason: details.stopReason
    };
  }

  /** A refusal's { "error": "…" } as a failure with its status. */
  function refused(response) {
    return response.text().then(function (body) {
      var message = '';
      try { message = JSON.parse(body).error || ''; } catch (notJson) { /* the status says enough */ }
      throw failure(message || 'The chat failed with HTTP ' + response.status + '.', response.status);
    });
  }

  /**
   * Reads the server-sent events of a streamed answer: {"text":"…"} per piece, then
   * {"done":true,…} with the model and the token counts, or {"error":"…"} if the provider
   * failed part way through. The server writes each event as one data: line and a blank line.
   */
  function readEvents(response, onText) {
    var text = '';
    var done = null;
    var pending = '';

    // One event's data; true once the answer is complete.
    function take(data) {
      var event;
      try { event = JSON.parse(data); } catch (notJson) {
        throw failure('Part of the answer arrived in a form this page cannot read.', 502);
      }

      if (event.error) throw failure(String(event.error), 502);
      if (event.done) {
        done = event;
        return true;
      }

      if (typeof event.text === 'string' && event.text !== '') {
        text += event.text;
        if (onText) onText(event.text);
      }
      return false;
    }

    function dataOf(block) {
      var lines = block.split('\n');
      var data = [];
      for (var i = 0; i < lines.length; i++) {
        if (lines[i].indexOf('data:') === 0) data.push(lines[i].slice(5).replace(/^ /, ''));
      }
      return data.length ? data.join('\n') : null;
    }

    // Adds what arrived and takes every whole event in it; true once the answer is complete.
    function drain(chunk, last) {
      pending += chunk;

      var boundary;
      while ((boundary = pending.indexOf('\n\n')) !== -1) {
        var data = dataOf(pending.slice(0, boundary));
        pending = pending.slice(boundary + 2);
        if (data !== null && take(data)) return true;
      }

      if (last && pending !== '') {
        var rest = dataOf(pending);
        pending = '';
        if (rest !== null && take(rest)) return true;
      }
      return false;
    }

    function finish() {
      if (!done) throw failure('The answer stopped before it was complete.', 502);
      return answer(text, done);
    }

    var body = response.body;
    if (!body || typeof body.getReader !== 'function' || typeof TextDecoder !== 'function') {
      return response.text().then(function (all) {
        drain(all, true);
        return finish();
      });
    }

    var reader = body.getReader();
    var decoder = new TextDecoder();
    function ignore() {}

    function pump() {
      return reader.read().then(function (step) {
        var complete = step.done
          ? drain(decoder.decode(), true)
          : drain(decoder.decode(step.value, { stream: true }), false);

        if (!complete && !step.done) return pump();
        if (!step.done) reader.cancel().then(null, ignore);
        return finish();
      });
    }

    return pump().then(null, function (error) {
      // Stop reading: an error event, an onText that threw, or the chat was stopped.
      try { reader.cancel().then(null, ignore); } catch (released) { /* already closed */ }
      throw error;
    });
  }

  /**
   * Sends a conversation and resolves with { text, model, inputTokens, outputTokens, stopReason }.
   *
   *   messages          an array of { role: 'user' | 'assistant', content }, oldest first, or a
   *                     string, which is one user message
   *   options.onText    called with each piece of the answer as it arrives
   *   options.system    instructions of the page's own, after the site's system prompt
   *   options.signal    an AbortSignal that stops the chat
   *   options.stream    false to wait for the whole answer instead; onText then gets it in one piece
   *
   * Rejects with an Error whose status is the HTTP status behind it: 403 when the site does not
   * let visitors chat, 404 when it has no provider, 429 when this address has asked too often
   * (the Retry-After header says for how long), 502 when the provider failed, and 0 when the
   * request never got an answer or was stopped.
   */
  function chat(messages, options) {
    options = options || {};
    var onText = typeof options.onText === 'function' ? options.onText : null;
    var stream = options.stream !== false;

    if (typeof fetch !== 'function') {
      return Promise.reject(failure('This browser cannot chat: it has no fetch.', 0));
    }
    if (typeof messages === 'string') messages = [{ role: 'user', content: messages }];

    var body = { messages: messages, stream: stream };
    if (options.system != null) body.system = String(options.system);

    return fetch(chatPath, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Accept': stream ? 'text/event-stream' : 'application/json' },
      body: JSON.stringify(body),
      credentials: 'same-origin',
      signal: options.signal
    }).then(function (response) {
      if (!response.ok) return refused(response);

      if ((response.headers.get('Content-Type') || '').indexOf('text/event-stream') === 0) {
        return readEvents(response, onText);
      }

      return response.json().then(function (whole) {
        if (onText && whole.text) onText(whole.text);
        return answer(whole.text || '', whole);
      });
    }).then(null, function (error) {
      throw asFailure(error);
    });
  }

  var ai = freeze({
    /** True when this site has an AI provider and lets its visitors chat, or lets its functions decide who may. */
    enabled: aiSettings.enabled === true,

    chat: chat
  });

  // ---- window.site ----------------------------------------------------------

  window.site = freeze({
    /** The domain this page was served for. */
    domain: String(host.domain || window.location.hostname),

    /** Public variables by name. Names are case-sensitive. */
    vars: vars,

    /** The version of the host that served this file. */
    version: String(host.version || ''),

    get: get,

    /** Events from the site's server side. See the realtime section above. */
    realtime: realtime,

    /** Chat through the site's AI provider. See chat() above. */
    ai: ai
  });
})(window);
