# Included in the trusted guest parent only. This is process lifecycle control,
# not an agent hook or a promise that a project/run succeeded.
sub notify_agent_exit {
    return 0 unless $status;
    my $wire = ($hook_partial ? "\n" : '') . "AGENT-EXITED\n";
    my $offset = 0;
    my $deadline = Time::HiRes::clock_gettime(Time::HiRes::CLOCK_MONOTONIC()) + 0.05;
    my $ready = IO::Select->new($status);
    local $SIG{PIPE} = 'IGNORE';
    while ($offset < length($wire)) {
        my $remaining = $deadline - Time::HiRes::clock_gettime(Time::HiRes::CLOCK_MONOTONIC());
        last if $remaining <= 0 || !$ready->can_write($remaining);
        my $count = syswrite($status, $wire, length($wire) - $offset, $offset);
        if (!defined $count) { next if $!{EINTR} || $!{EAGAIN}; last; }
        last if $count == 0;
        $offset += $count;
    }
    if ($offset == length($wire)) { $hook_partial = 0; return 1; }
    # On a partial control frame do not append later AUTH/HOME headers into it.
    if ($offset > 0) { close $status; $status = undef; }
    mark('AGENT-EXIT-NOTICE-FAILED');
    return 0;
}
