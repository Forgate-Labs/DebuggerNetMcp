#include <sys/ptrace.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <errno.h>
#include <stddef.h>

#define EXPORT __attribute__((visibility("default")))

/*
 * Attach to a running process using PTRACE_SEIZE.
 * PTRACE_SEIZE does NOT stop the process (unlike PTRACE_ATTACH which sends SIGSTOP).
 * Required for kernel 6.12+ compatibility — PTRACE_ATTACH causes race conditions
 * with ICorDebug's libdbgshim callback mechanism.
 */
EXPORT int dbg_attach(pid_t pid) {
    if (ptrace(PTRACE_SEIZE, pid, NULL, NULL) == -1)
        return -errno;
    return 0;
}

/*
 * Detach from a traced process, resuming its execution.
 */
EXPORT int dbg_detach(pid_t pid) {
    if (ptrace(PTRACE_DETACH, pid, NULL, NULL) == -1)
        return -errno;
    return 0;
}

/*
 * Interrupt a running process that was seized with PTRACE_SEIZE.
 * PTRACE_INTERRUPT is only valid after PTRACE_SEIZE; it replaces the old
 * SIGSTOP approach and avoids signal-delivery races.
 */
EXPORT int dbg_interrupt(pid_t pid) {
    if (ptrace(PTRACE_INTERRUPT, pid, NULL, NULL) == -1)
        return -errno;
    return 0;
}

/*
 * Resume a stopped traced process, delivering optional signal sig (0 = no signal).
 */
EXPORT int dbg_continue(pid_t pid, int sig) {
    if (ptrace(PTRACE_CONT, pid, NULL, (void*)(long)sig) == -1)
        return -errno;
    return 0;
}

/*
 * Wait for a traced process to change state.
 * Returns the pid of the child that changed state, or -errno on error.
 */
EXPORT int dbg_wait(pid_t pid, int *status, int flags) {
    pid_t result = waitpid(pid, status, flags);
    if (result == -1)
        return -errno;
    return (int)result;
}
