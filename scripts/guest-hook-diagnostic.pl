# Included verbatim into the trusted guest parent by stage-hook-diagnostics.py.
# Runtime hook children never inherit or open the privileged virtio status FD.
my ($hook_log, $hook_path, $hook_nonce);
my $hook_pending = '';
my $hook_dropped = 0;
my $hook_partial = 0;
if ($agent eq 'grok') {
    eval {
        open my $random, '<', '/dev/urandom' or die;
        my $bytes; read($random, $bytes, 16) == 16 or die; close $random;
        $hook_nonce = unpack('H*', $bytes);
        ($hook_log, $hook_path) = File::Temp::tempfile('launchpad-hooks-XXXXXXXX', DIR => '/tmp', UNLINK => 0);
        binmode $hook_log;
        # Root keeps the read handle; builder can append only to this disposable log.
        chown 0, 1000, $hook_path or die;
        chmod 0620, $hook_path or die;
        $ENV{LP_HOOK_NONCE} = $hook_nonce;
        $ENV{LP_HOOK_LOG} = $hook_path;
        mark('HOOK-DIAGNOSTICS-STAGED');
        1;
    } or do { $hook_nonce = undef; mark('HOOK-DIAGNOSTICS-UNAVAILABLE'); };
}

sub configure_hook_diagnostic {
    return unless $hook_nonce;
    close $hook_log if $hook_log;
    # This function is called only AFTER drop_to_builder(), never as root.
    system '/usr/bin/python3', '-I', '/usr/local/lib/launchpad/guest-hook-diagnostic.py',
        'setup', $hook_nonce, $hook_path;
}

sub relay_hook_diagnostics {
    return unless $hook_log && $status && $hook_nonce;
    my $chunk;
    my $count = sysread($hook_log, $chunk, 8192);
    $hook_pending .= $chunk if defined($count) && $count > 0;
    my $budget = 8;
    while ($budget-- > 0 && $hook_pending =~ s/\A([^\n]*)\n//) {
        my $line = $1;
        next if length($line) > 4096;
        my $row = eval { decode_json($line) };
        next unless ref($row) eq 'HASH' && ($row->{nonce} // '') eq $hook_nonce;
        my %names = map { $_ => 1 } qw(hookEventName hook_event_name sessionId session_id turnId turn_id
            promptId prompt_id notificationType notification_type type message title cwd workspaceRoot
            workspace_root toolName toolInput tool_name tool_input toolUseId requestId agentId subagentId
            parentSessionId stopHookActive stop_hook_active backgroundTasks background_tasks sessionCrons
            session_crons reason stopReason status);
        my %types = map { $_ => 1 } qw(str int float bool list dict NoneType);
        my %events = map { $_ => 1 } qw(SessionStart SessionEnd UserPromptSubmit PreToolUse PostToolUse
            PostToolUseFailure PermissionDenied Stop StopFailure Notification SubagentStart SubagentStop
            PreCompact PostCompact Unknown);
        my %notices = map { $_ => 1 } qw(permission_prompt idle_prompt elicitation_dialog auth_success info
            warning task_complete other absent);
        next unless ($row->{version} // '') eq '1' && !ref($row->{event})
            && $events{$row->{event} // ''} && ref($row->{fields}) eq 'HASH'
            && !ref($row->{notificationType}) && $notices{$row->{notificationType} // ''};
        my %fields;
        for my $key (keys %{$row->{fields}}) {
            my $type = $row->{fields}{$key};
            $fields{$key} = $type if $names{$key} && !ref($type) && $types{$type // ''};
        }
        my %safe = (version => 1, nonce => $hook_nonce, event => $row->{event}, fields => \%fields,
            notificationType => $row->{notificationType});
        for my $key (qw(capturedUnixMs backgroundTasksCount sessionCronsCount)) {
            my $value = $row->{$key};
            next unless defined($value) && !ref($value) && $value =~ /\A-?\d{1,13}\z/;
            $safe{$key} = 0 + $value;
        }
        next unless exists($safe{capturedUnixMs}) && exists($safe{backgroundTasksCount}) && exists($safe{sessionCronsCount});
        for my $key (qw(sessionHash turnHash)) {
            my $value = $row->{$key};
            $safe{$key} = defined($value) && !ref($value) && $value =~ /\A[0-9a-f]{64}\z/ ? $value : undef;
        }
        $safe{stopHookActive} = JSON::PP::is_bool($row->{stopHookActive}) ? $row->{stopHookActive} : undef;
        $safe{cwdMatchesProject} = JSON::PP::is_bool($row->{cwdMatchesProject}) ? $row->{cwdMatchesProject} : JSON::PP::false;
        # The Windows decoder makes a typed allowlisted projection before logging.
        # Never use guest values as command text, paths, permissions, or state.
        my $wire = 'LP-DIAG ' . encode_json(\%safe) . "\n";
        my $ready = IO::Select->new($status);
        unless ($ready->can_write(0)) { $hook_dropped++; next; }
        my $written = syswrite($status, $wire);
        if (!defined($written) || $written != length($wire)) {
            # A partial diagnostic line must not merge with AUTH/HOME framing.
            # Stop diagnostics after any pressure; mandatory protocol is untouched.
            $hook_dropped++;
            $hook_partial = 1 if defined($written) && $written > 0;
            $hook_nonce = undef;
            mark('HOOK-DIAGNOSTICS-TRANSPORT-INCOMPLETE');
            last;
        }
    }
    if (length($hook_pending) > 16384) { $hook_pending = ''; $hook_dropped++; }
}

sub cleanup_hook_diagnostic {
    return unless $ENV{LP_HOOK_NONCE};
    # Capture files remain for guest-side inspection. Remove only the exact
    # unchanged hook we added, using the unprivileged identity.
    my $cleanup = fork();
    if (defined($cleanup) && $cleanup == 0) {
        close $status if $status;
        close_guest_devices();
        drop_to_builder();
        exec '/usr/bin/python3', '-I', '/usr/local/lib/launchpad/guest-hook-diagnostic.py',
            'cleanup', $ENV{LP_HOOK_NONCE}, $ENV{LP_HOOK_LOG};
        exit 0;
    }
    waitpid $cleanup, 0 if defined($cleanup) && $cleanup > 0;
    mark('HOOK-DIAGNOSTICS-DROPPED ' . $hook_dropped) if $hook_dropped;
}
