# Trusted guest parent only. Checkpoint private Grok login without depending on
# terminal close. The sole parent writer keeps AUTH/HOME/diagnostic framing.
use Time::HiRes ();
my ($auth_stamp, $auth_checked) = ('', 0);

sub read_private_auth {
    my $directory;
    return unless sysopen($directory, '/home', Fcntl::O_RDONLY() | Fcntl::O_DIRECTORY() | Fcntl::O_NOFOLLOW());
    for my $name ('builder', '.grok') {
        my $next;
        my $path = '/proc/self/fd/' . fileno($directory) . '/' . $name;
        unless (sysopen($next, $path, Fcntl::O_RDONLY() | Fcntl::O_DIRECTORY() | Fcntl::O_NOFOLLOW())) {
            close $directory; return;
        }
        close $directory;
        $directory = $next;
        my @info = stat $directory;
        unless (@info && $info[4] == 1000 && ($info[2] & 0022) == 0) { close $directory; return; }
    }
    my $in;
    my $path = '/proc/self/fd/' . fileno($directory) . '/auth.json';
    unless (sysopen($in, $path, Fcntl::O_RDONLY() | Fcntl::O_NOFOLLOW() | Fcntl::O_NONBLOCK())) {
        close $directory; return;
    }
    close $directory;
    binmode $in;
    my @info = stat $in;
    unless (@info && ($info[2] & 0170000) == 0100000 && $info[3] == 1 && $info[4] == 1000
        && ($info[2] & 0077) == 0 && $info[7] > 0 && $info[7] <= 1048576) { close $in; return; }
    my $body = '';
    while (length($body) <= 1048576) {
        my $chunk;
        my $count = sysread($in, $chunk, 65536);
        if (!defined $count) { $body = ''; last; }
        last if $count == 0;
        $body .= $chunk;
    }
    my @after = stat $in;
    close $in;
    return unless length($body) > 0 && length($body) <= 1048576 && @after
        && join(':', @info[0, 1, 7, 9, 10]) eq join(':', @after[0, 1, 7, 9, 10]);
    return ($body, join(':', @after[0, 1, 7, 9, 10]));
}

sub write_auth_frame {
    my ($body) = @_;
    return 0 unless $status;
    my $wire = ($hook_partial ? "\n" : '') . 'AUTH ' . length($body) . "\n" . $body;
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
    if ($offset > 0) {
        # Never append later headers into an incomplete credential body. Stop
        # this status link; project/session recovery uses separate channels.
        close $status;
        $status = undef;
        mark('AUTH-TRANSPORT-INCOMPLETE');
    }
    return 0;
}

sub relay_auth_updates {
    my ($force) = @_;
    return unless $agent eq 'grok' && $status;
    my $now = time;
    return if !$force && $now - $auth_checked < 2;
    $auth_checked = $now;
    my ($body, $stamp) = read_private_auth();
    return unless defined($body) && $stamp ne $auth_stamp;
    $auth_stamp = $stamp if write_auth_frame($body);
}

sub send_auth {
    return unless $status;
    my ($body) = read_private_auth();
    write_auth_frame($body // '');
}
