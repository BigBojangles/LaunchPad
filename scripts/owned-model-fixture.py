#!/usr/bin/env python3
"""Controlled loopback LLM wire fixture: one owned shell call, then completion."""
import json
import pathlib
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

def tool_call(tools, agent):
    candidates = [tool.get('function', tool) for tool in tools]
    for preferred in ['Bash', 'exec_command', 'shell_command', 'terminal', 'shell', 'run_terminal_command']:
        for tool in candidates:
            if tool.get('name', '').lower() != preferred.lower():
                continue
            schema = tool.get('parameters', tool.get('input_schema', {}))
            properties = schema.get('properties', {})
            helper = 'owned-agent-interruption.py' if pathlib.Path('owned_terminal.json').exists() else 'owned-agent-boundary.py'
            command = 'python3 ' + helper + ' ' + agent
            values = {}
            for name in ['cmd', 'command', 'commands']:
                if name in properties:
                    values[name] = [command] if properties[name].get('type') == 'array' else command
                    break
            if not values:
                continue
            for name, value in [('description', 'Run the owned LaunchPad permission fixture'),
                                ('workdir', '/home/builder/in/project'), ('cwd', '/home/builder/in/project'),
                                ('yield_time_ms', 1000), ('max_output_tokens', 2000),
                                ('timeout', 120000 if pathlib.Path('owned_terminal.json').exists() else 10000)]:
                if name in properties:
                    values[name] = value
            return tool['name'], values
    raise RuntimeError('No offered shell tool: ' + ','.join(tool.get('name', '?') for tool in candidates))

def pending_session(body):
    # Codex may yield a running command. Poll that exact owned session rather
    # than requesting another command or finishing while its child is active.
    for item in reversed(body.get('input', [])):
        if not isinstance(item, dict) or item.get('type') != 'function_call_output':
            continue
        output = item.get('output', '')
        if isinstance(output, str):
            match = re.search(r'(?:Process|Script) running with (?:session|cell) ID (\d+)', output, re.IGNORECASE)
            if match:
                return int(match.group(1))
            try:
                output = json.loads(output)
            except (ValueError, TypeError):
                return None
        return output.get('session_id') if isinstance(output, dict) else None
    return None

def poll_call(tools, session):
    for raw in tools:
        tool = raw.get('function', raw)
        if tool.get('name') == 'write_stdin':
            return tool['name'], dict(session_id=session, chars='', yield_time_ms=1000, max_output_tokens=2000)
    raise RuntimeError('Owned running command requires offered write_stdin tool')

class Fixture(ThreadingHTTPServer):
    daemon_threads = True
    def __init__(self, agent, output):
        super().__init__(('127.0.0.1', 0), Handler)
        self.agent, self.output = agent, output
        self.records = []
        self.calls = 0
        self.command_calls = 0
        self.returned = False
        self.lock = threading.Lock()

    def record(self, **entry):
        with self.lock:
            self.records.append(entry)
            (self.output / (self.agent + '-wire.json')).write_text(json.dumps(self.records, indent=2) + '\n')

class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *_):
        pass

    def respond(self, body, content='application/json'):
        encoded = body.encode()
        self.send_response(200)
        self.send_header('Content-Type', content)
        self.send_header('Content-Length', str(len(encoded)))
        self.send_header('Connection', 'close')
        self.end_headers()
        self.wfile.write(encoded)
        self.close_connection = True

    def do_CONNECT(self):
        self.server.record(blockedProxy=True)
        self.send_error(403, 'Owned fixture does not forward traffic')

    def do_GET(self):
        self.server.record(method='GET', path=self.path)
        if self.path == '/owned-health':
            self.respond(json.dumps(dict(agent=self.server.agent)))
        elif 'models' in self.path:
            self.respond(json.dumps(dict(object='list', data=[dict(id='launchpad-fixture', object='model', owned_by='owned-fixture')])))
        else:
            self.send_error(404)

    def do_POST(self):
        if self.path.startswith(('http:', 'https:')):
            self.server.record(blockedProxy=True)
            self.send_error(403)
            return
        length = int(self.headers.get('Content-Length', '0'))
        if not 0 < length < 4 * 1024 * 1024:
            self.send_error(400)
            return
        body = json.loads(self.rfile.read(length))
        path = urlsplit(self.path).path
        tools = body.get('tools', [])
        names = [tool.get('function', tool).get('name', '?') for tool in tools]
        has_result = 'OWNED-AGENT-BOUNDARY-DONE:' + self.server.agent in json.dumps(body)
        session = pending_session(body) if path.endswith('/responses') and not has_result else None
        self.server.record(path=self.path, tools=names, model=body.get('model'), hasToolResult=has_result, pendingSession=session)
        self.server.returned |= has_result
        if path.endswith('/count_tokens'):
            self.respond('{"input_tokens": 100}')
            return
        try:
            shell = (poll_call(tools, session) if session is not None else tool_call(tools, self.server.agent)) if tools and not has_result else None
        except RuntimeError as error:
            self.server.record(error=str(error))
            shell = None
        if shell:
            self.server.calls += 1
            self.server.command_calls += 0 if session is not None else 1
            limit = 120 if pathlib.Path('owned_terminal.json').exists() else 15
            if self.server.command_calls > 1 or self.server.calls > limit:
                self.send_error(400, 'Owned tool did not finish successfully')
                return
        if path.endswith('/messages'):
            self.messages(body, shell)
        elif path.endswith('/responses'):
            self.response_stream(body, shell)
        elif path.endswith('/chat/completions'):
            self.chat(body, shell)
        else:
            self.send_error(404)

    def messages(self, body, shell):
        model = body.get('model', 'owned-fixture')
        content = dict(type='tool_use', id='toolu_owned_1', name=shell[0], input=shell[1]) if shell else dict(type='text', text='Owned fixture complete.')
        reason = 'tool_use' if shell else 'end_turn'
        message = dict(id='msg_owned_1', type='message', role='assistant', model=model, content=[content], stop_reason=reason,
                       stop_sequence=None, usage=dict(input_tokens=100, output_tokens=20))
        if not body.get('stream'):
            self.respond(json.dumps(message))
            return
        events = [('message_start', dict(type='message_start', message={**message, 'content': [], 'stop_reason': None})),
                  ('content_block_start', dict(type='content_block_start', index=0, content_block=({**content, 'input': {}} if shell else dict(type='text', text='')))),
                  ('content_block_delta', dict(type='content_block_delta', index=0, delta=(dict(type='input_json_delta', partial_json=json.dumps(shell[1])) if shell else dict(type='text_delta', text=content['text'])))),
                  ('content_block_stop', dict(type='content_block_stop', index=0)),
                  ('message_delta', dict(type='message_delta', delta=dict(stop_reason=reason, stop_sequence=None), usage=dict(output_tokens=20))),
                  ('message_stop', dict(type='message_stop'))]
        self.respond(''.join('event: ' + name + '\ndata: ' + json.dumps(value) + '\n\n' for name, value in events), 'text/event-stream')

    def response_stream(self, body, shell):
        call_id = str(self.server.calls)
        item = dict(type='function_call', id='fc_owned_' + call_id, call_id='call_owned_' + call_id, name=shell[0], arguments=json.dumps(shell[1]), status='completed') if shell else dict(
            type='message', id='msg_owned_1', role='assistant', status='completed', content=[dict(type='output_text', text='Owned fixture complete.', annotations=[])])
        response = dict(id='resp_owned_' + str(self.server.calls) + ('_done' if not shell else ''), object='response', model=body.get('model'),
                        status='completed', output=[item], usage=dict(input_tokens=100, output_tokens=20, total_tokens=120,
                        input_tokens_details=dict(cached_tokens=0), output_tokens_details=dict(reasoning_tokens=0)))
        events = [dict(type='response.created', response={**response, 'output': [], 'status': 'in_progress'}),
                  dict(type='response.output_item.added', output_index=0, item={**item, 'arguments': ''} if shell else item)]
        if shell:
            events += [dict(type='response.function_call_arguments.delta', item_id=item['id'], output_index=0, delta=item['arguments']),
                       dict(type='response.function_call_arguments.done', item_id=item['id'], output_index=0, arguments=item['arguments'])]
        else:
            events += [dict(type='response.output_text.delta', item_id=item['id'], output_index=0, content_index=0, delta=item['content'][0]['text'])]
        events += [dict(type='response.output_item.done', output_index=0, item=item), dict(type='response.completed', response=response)]
        self.respond(''.join('data: ' + json.dumps({**event, 'sequence_number': index}) + '\n\n' for index, event in enumerate(events)), 'text/event-stream')

    def chat(self, body, shell):
        message = dict(role='assistant', content=None, tool_calls=[dict(id='call_owned_1', type='function', function=dict(name=shell[0], arguments=json.dumps(shell[1])))]) if shell else dict(role='assistant', content='Owned fixture complete.')
        chunk = dict(id='chatcmpl_owned', object='chat.completion', created=int(time.time()), model=body.get('model'),
                     choices=[dict(index=0, message=message, finish_reason='tool_calls' if shell else 'stop')], usage=dict(prompt_tokens=100, completion_tokens=20, total_tokens=120))
        if not body.get('stream'):
            self.respond(json.dumps(chunk))
            return
        delta = dict(role='assistant', tool_calls=[dict(index=0, id='call_owned_1', type='function', function=dict(name=shell[0], arguments=json.dumps(shell[1])))]) if shell else dict(role='assistant', content=message['content'])
        streamed = {**chunk, 'object': 'chat.completion.chunk', 'choices': [dict(index=0, delta=delta, finish_reason=None)]}
        end = {**streamed, 'choices': [dict(index=0, delta={}, finish_reason='tool_calls' if shell else 'stop')]}
        self.respond('data: ' + json.dumps(streamed) + '\n\ndata: ' + json.dumps(end) + '\n\ndata: [DONE]\n\n', 'text/event-stream')

def configure(agent, environment, output, port, helper='owned-agent-boundary.py'):
    (output / (agent + '-targets.json')).write_text(json.dumps(dict(loopbackPort=port)))
    base = 'http://127.0.0.1:' + str(port)
    environment.update(LP_FIXTURE_KEY='launchpad-owned-fixture-not-a-secret', ANTHROPIC_API_KEY='launchpad-owned-fixture-not-a-secret',
                       ANTHROPIC_BASE_URL=base, HTTP_PROXY=base, HTTPS_PROXY=base, NO_PROXY='127.0.0.1,localhost',
                       http_proxy=base, https_proxy=base, no_proxy='127.0.0.1,localhost')
    prompt = 'Run exactly python3 ' + helper + ' ' + agent + ' through your shell tool, then finish. This is an owned permission fixture.'
    if agent == 'grok':
        config = pathlib.Path('/home/builder/.grok/config.toml')
        config.parent.mkdir(exist_ok=True)
        config.write_text('[models]\ndefault = "launchpad-fixture"\n[model."launchpad-fixture"]\nmodel = "launchpad-fixture"\nbase_url = "' + base + '/v1"\nenv_key = "LP_FIXTURE_KEY"\napi_backend = "chat_completions"\ncontext_window = 128000\n')
        command = ['grok', '-p', prompt, '--model', 'launchpad-fixture', '--always-approve', '--no-subagents', '--disable-web-search', '--max-turns', '3']
    elif agent == 'codex':
        config = pathlib.Path('/home/builder/.codex/config.toml')
        config.parent.mkdir(exist_ok=True)
        config.write_text('model = "gpt-5-codex"\nmodel_provider = "owned"\n[model_providers.owned]\nname = "Owned loopback fixture"\nbase_url = "' + base + '/v1"\nenv_key = "LP_FIXTURE_KEY"\nwire_api = "responses"\nrequires_openai_auth = false\nsupports_websockets = false\nrequest_max_retries = 0\nstream_max_retries = 0\n')
        command = ['codex', '--no-daemon', '-a', 'never', 'exec', '--skip-git-repo-check', '--sandbox', 'danger-full-access', prompt]
    else:
        command = ['claude', '-p', prompt, '--tools', 'Bash', '--allowedTools', 'Bash(python3 ' + helper + ' *)', '--permission-prompts', 'none', '--output-format', 'json']
    return command

def exercise(agent, confined, environment, output):
    server = Fixture(agent, output)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        command = configure(agent, environment, output, server.server_address[1])
        result = confined(command, agent + '-tool', timeout=60)
        proof = output / (agent + '-boundary.json')
        result['agent'] = agent
        result['toolProof'] = proof.exists() and json.loads(proof.read_text()).get('passed') is True and server.returned
        result['controlledCalls'] = server.calls
        return result
    finally:
        server.shutdown()
        server.server_close()
