// A native supervisor avoids requiring a shell/Python in the installed plugin.
// The inference process has its own process group so teardown includes children.
#include <cerrno>
#include <csignal>
#include <cstdlib>
#include <spawn.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

extern char** environ;
static volatile sig_atomic_t stopping = 0;
static void stop(int) { stopping = 1; }

static void clean_group(pid_t child, bool& reaped, int& status) {
    kill(-child, SIGTERM);
    const struct timespec pause = {0, 100000000};
    for (int attempt = 0; attempt < 50; ++attempt) {
        if (!reaped && waitpid(child, &status, WNOHANG) == child) reaped = true;
        // Reaping the group leader does not imply its descendants have exited.
        if (kill(-child, 0) < 0 && errno == ESRCH) return;
        nanosleep(&pause, nullptr);
    }
    kill(-child, SIGKILL);
    if (!reaped) {
        while (waitpid(child, &status, 0) < 0 && errno == EINTR) {}
        reaped = true;
    }
}

int main(int argc, char** argv) {
    if (argc < 3) return 64;
    char* end = nullptr;
    const long requested_parent = std::strtol(argv[1], &end, 10);
    if (!end || *end || requested_parent <= 1 || getppid() != requested_parent) return 64;
    struct sigaction action = {};
    action.sa_handler = stop;
    sigemptyset(&action.sa_mask);
    sigaction(SIGTERM, &action, nullptr);
    sigaction(SIGINT, &action, nullptr);

    posix_spawnattr_t attributes;
    posix_spawnattr_init(&attributes);
    posix_spawnattr_setflags(&attributes, POSIX_SPAWN_SETPGROUP);
    posix_spawnattr_setpgroup(&attributes, 0);
    pid_t child = 0;
    const int error = posix_spawn(&child, argv[2], nullptr, &attributes, argv + 2, environ);
    posix_spawnattr_destroy(&attributes);
    if (error) return 70;

    const struct timespec pause = {0, 100000000};
    int status = 0;
    bool reaped = false;
    bool wait_failed = false;
    while (!stopping && getppid() == requested_parent) {
        const pid_t result = waitpid(child, &status, WNOHANG);
        if (result == child) { reaped = true; break; }
        if (result < 0 && errno != EINTR) { wait_failed = true; break; }
        nanosleep(&pause, nullptr);
    }
    clean_group(child, reaped, status);
    if (wait_failed) return 70;
    if (stopping || getppid() != requested_parent) return 0;
    return WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
}
