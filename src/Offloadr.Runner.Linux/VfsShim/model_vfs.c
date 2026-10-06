#define _GNU_SOURCE
#define _LARGEFILE64_SOURCE
#include <aio.h>
#include <arpa/inet.h>
#include <dlfcn.h>
#include <endian.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/fiemap.h>
#include <linux/fs.h>
#include <pthread.h>
#include <sched.h>
#include <spawn.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/ioctl.h>
#include <sys/sendfile.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/sysmacros.h>
#include <sys/types.h>
#include <sys/uio.h>
#include <sys/un.h>
#include <unistd.h>
#include <wchar.h>

#define VFS_MAGIC 0x4f564653u
#define VFS_VERSION 3u
#define VFS_HEADER_LENGTH 12u
#define VFS_RESPONSE_LENGTH 52u
#define VFS_MAX_PAYLOAD (32u * 1024u)

#define VFS_OP_OPEN 1u
#define VFS_OP_ENSURE_RANGE 2u
#define VFS_OP_ENSURE_COMPLETE 3u
#define VFS_OP_RELEASE 4u
#define VFS_OP_ACKNOWLEDGE_OPEN 5u

#define VFS_STATUS_SUCCESS 0u
#define VFS_STATUS_NOT_MANAGED 7u
#define VFS_STATUS_IO_ERROR 8u
#define VFS_STATUS_READ_ONLY 9u

#define VFS_OPEN_UNMANAGED 0u
#define VFS_OPEN_FULL_READY 1u
#define VFS_OPEN_RANGE_MANAGED 2u

#ifndef CLOSE_RANGE_UNSHARE
#define CLOSE_RANGE_UNSHARE (1U << 1)
#endif
#ifndef CLOSE_RANGE_CLOEXEC
#define CLOSE_RANGE_CLOEXEC (1U << 2)
#endif

typedef struct interval {
    uint64_t start;
    uint64_t end;
    struct interval *next;
} interval_t;

typedef struct shared_state {
    pthread_mutex_t io_lock;
    volatile int references;
    uint64_t lease_id;
    int64_t epoch;
    int64_t expected_length;
    int complete;
    interval_t *readable;
} shared_state_t;

typedef struct fd_entry {
    int fd;
    shared_state_t *state;
    struct fd_entry *next;
} fd_entry_t;

typedef struct pending_release {
    uint64_t lease_id;
    struct pending_release *next;
} pending_release_t;

typedef struct spawn_dup_action {
    int source_fd;
    struct spawn_dup_action *next;
} spawn_dup_action_t;

typedef struct spawn_open_action {
    char *path;
    struct spawn_open_action *next;
} spawn_open_action_t;

typedef struct spawn_actions_entry {
    posix_spawn_file_actions_t *actions;
    spawn_dup_action_t *dup_actions;
    spawn_open_action_t *open_actions;
    int directory_changed;
    struct spawn_actions_entry *next;
} spawn_actions_entry_t;

typedef struct ipc_response {
    uint8_t status;
    uint8_t disposition;
    uint64_t lease_id;
    int64_t epoch;
    int64_t expected_length;
    uint64_t device_id;
    uint64_t inode;
} ipc_response_t;

static void request_release(uint64_t lease_id);

static int (*real_openat_fn)(int, const char *, int, ...) = NULL;
static int (*real_close_fn)(int) = NULL;
static int (*real_close_range_fn)(unsigned int, unsigned int, int) = NULL;
static int (*real_fcloseall_fn)(void) = NULL;
static ssize_t (*real_read_fn)(int, void *, size_t) = NULL;
static ssize_t (*real_pread_fn)(int, void *, size_t, off_t) = NULL;
static ssize_t (*real_pread64_fn)(int, void *, size_t, off64_t) = NULL;
static ssize_t (*real_readv_fn)(int, const struct iovec *, int) = NULL;
static ssize_t (*real_preadv_fn)(int, const struct iovec *, int, off_t) = NULL;
static ssize_t (*real_preadv2_fn)(int, const struct iovec *, int, off_t, int) = NULL;
static ssize_t (*real_preadv64_fn)(int, const struct iovec *, int, off64_t) = NULL;
static ssize_t (*real_preadv64v2_fn)(int, const struct iovec *, int, off64_t, int) = NULL;
static ssize_t (*real___read_chk_fn)(int, void *, size_t, size_t) = NULL;
static ssize_t (*real___pread_chk_fn)(int, void *, size_t, off_t, size_t) = NULL;
static ssize_t (*real___pread64_chk_fn)(int, void *, size_t, off64_t, size_t) = NULL;
static int (*real_aio_read_fn)(struct aiocb *) = NULL;
static int (*real_aio_read64_fn)(struct aiocb64 *) = NULL;
static int (*real_lio_listio_fn)(int, struct aiocb *const[], int, struct sigevent *) = NULL;
static int (*real_lio_listio64_fn)(int, struct aiocb64 *const[], int, struct sigevent *) = NULL;
static ssize_t (*real_sendfile_fn)(int, int, off_t *, size_t) = NULL;
static ssize_t (*real_sendfile64_fn)(int, int, off64_t *, size_t) = NULL;
static ssize_t (*real_copy_file_range_fn)(int, off64_t *, int, off64_t *, size_t, unsigned int) = NULL;
static ssize_t (*real_splice_fn)(int, off64_t *, int, off64_t *, size_t, unsigned int) = NULL;
static ssize_t (*real_sendmsg_fn)(int, const struct msghdr *, int) = NULL;
static int (*real_sendmmsg_fn)(int, struct mmsghdr *, unsigned int, int) = NULL;
static int (*real_ioctl_fn)(int, unsigned long, ...) = NULL;
static off_t (*real_lseek_fn)(int, off_t, int) = NULL;
static off64_t (*real_lseek64_fn)(int, off64_t, int) = NULL;
static int (*real_dup_fn)(int) = NULL;
static int (*real_dup2_fn)(int, int) = NULL;
static int (*real_dup3_fn)(int, int, int) = NULL;
static int (*real_fcntl_fn)(int, int, ...) = NULL;
static int (*real_fcntl64_fn)(int, int, ...) = NULL;
static pid_t (*real_fork_fn)(void) = NULL;
static pid_t (*real_vfork_fn)(void) = NULL;
static int (*real_clone_fn)(int (*)(void *), void *, int, void *, ...) = NULL;
static int (*real_posix_spawn_fn)(
    pid_t *,
    const char *,
    const posix_spawn_file_actions_t *,
    const posix_spawnattr_t *,
    char *const[],
    char *const[]) = NULL;
static int (*real_posix_spawnp_fn)(
    pid_t *,
    const char *,
    const posix_spawn_file_actions_t *,
    const posix_spawnattr_t *,
    char *const[],
    char *const[]) = NULL;
static int (*real_posix_spawn_file_actions_init_fn)(posix_spawn_file_actions_t *) = NULL;
static int (*real_posix_spawn_file_actions_destroy_fn)(posix_spawn_file_actions_t *) = NULL;
static int (*real_posix_spawn_file_actions_adddup2_fn)(
    posix_spawn_file_actions_t *,
    int,
    int) = NULL;
static int (*real_posix_spawn_file_actions_addopen_fn)(
    posix_spawn_file_actions_t *,
    int,
    const char *,
    int,
    mode_t) = NULL;
static int (*real_posix_spawn_file_actions_addchdir_np_fn)(
    posix_spawn_file_actions_t *,
    const char *) = NULL;
static int (*real_posix_spawn_file_actions_addfchdir_np_fn)(
    posix_spawn_file_actions_t *,
    int) = NULL;
static FILE *(*real_fopen_fn)(const char *, const char *) = NULL;
static FILE *(*real_fopen64_fn)(const char *, const char *) = NULL;
static FILE *(*real_freopen_fn)(const char *, const char *, FILE *) = NULL;
static FILE *(*real_freopen64_fn)(const char *, const char *, FILE *) = NULL;
static FILE *(*real_fdopen_fn)(int, const char *) = NULL;
static int (*real_fclose_fn)(FILE *) = NULL;
static int (*real_setvbuf_fn)(FILE *, char *, int, size_t) = NULL;
static void (*real_setbuf_fn)(FILE *, char *) = NULL;
static void (*real_setbuffer_fn)(FILE *, char *, size_t) = NULL;
static void (*real_setlinebuf_fn)(FILE *) = NULL;
static size_t (*real_fread_fn)(void *, size_t, size_t, FILE *) = NULL;
static size_t (*real_fread_unlocked_fn)(void *, size_t, size_t, FILE *) = NULL;
static size_t (*real___fread_chk_fn)(void *, size_t, size_t, size_t, FILE *) = NULL;
static size_t (*real___fread_unlocked_chk_fn)(void *, size_t, size_t, size_t, FILE *) = NULL;
static int (*real_fgetc_fn)(FILE *) = NULL;
static int (*real_getc_fn)(FILE *) = NULL;
static int (*real_fgetc_unlocked_fn)(FILE *) = NULL;
static int (*real_getc_unlocked_fn)(FILE *) = NULL;
static int (*real___uflow_fn)(FILE *) = NULL;
static char *(*real_fgets_fn)(char *, int, FILE *) = NULL;
static char *(*real_fgets_unlocked_fn)(char *, int, FILE *) = NULL;
static ssize_t (*real_getdelim_fn)(char **, size_t *, int, FILE *) = NULL;
static ssize_t (*real___getdelim_fn)(char **, size_t *, int, FILE *) = NULL;
static int (*real_vfscanf_fn)(FILE *, const char *, va_list) = NULL;
static int (*real___isoc99_vfscanf_fn)(FILE *, const char *, va_list) = NULL;
static int (*real___isoc23_vfscanf_fn)(FILE *, const char *, va_list) = NULL;
static wint_t (*real_fgetwc_fn)(FILE *) = NULL;
static wint_t (*real_getwc_fn)(FILE *) = NULL;
static wint_t (*real_fgetwc_unlocked_fn)(FILE *) = NULL;
static wint_t (*real_getwc_unlocked_fn)(FILE *) = NULL;
static wchar_t *(*real_fgetws_fn)(wchar_t *, int, FILE *) = NULL;
static wchar_t *(*real_fgetws_unlocked_fn)(wchar_t *, int, FILE *) = NULL;
static wchar_t *(*real___fgetws_chk_fn)(wchar_t *, size_t, int, FILE *) = NULL;
static wchar_t *(*real___fgetws_unlocked_chk_fn)(wchar_t *, size_t, int, FILE *) = NULL;
static int (*real_vfwscanf_fn)(FILE *, const wchar_t *, va_list) = NULL;
static int (*real___isoc99_vfwscanf_fn)(FILE *, const wchar_t *, va_list) = NULL;
static int (*real___isoc23_vfwscanf_fn)(FILE *, const wchar_t *, va_list) = NULL;
static void *(*real_mmap_fn)(void *, size_t, int, int, int, off_t) = NULL;
static void *(*real_mmap64_fn)(void *, size_t, int, int, int, off64_t) = NULL;
static int (*real_faccessat_fn)(int, const char *, int, int) = NULL;
static int (*real_statx_fn)(int, const char *, int, unsigned int, struct statx *) = NULL;

static pthread_mutex_t fd_map_lock = PTHREAD_MUTEX_INITIALIZER;
static fd_entry_t *fd_entries = NULL;
static pthread_mutex_t inheritance_lock = PTHREAD_MUTEX_INITIALIZER;
static int managed_descriptor_seen = 0;
static pthread_mutex_t spawn_actions_lock = PTHREAD_MUTEX_INITIALIZER;
static spawn_actions_entry_t *spawn_actions_entries = NULL;
static pthread_mutex_t pending_release_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t pending_release_ready = PTHREAD_COND_INITIALIZER;
static pending_release_t *pending_releases = NULL;
static int pending_release_worker_started = 0;
static __thread int reentry = 0;
static int log_state = -1;

static void init_syms(void) {
    if (!real_openat_fn) real_openat_fn = dlsym(RTLD_NEXT, "openat");
    if (!real_close_fn) real_close_fn = dlsym(RTLD_NEXT, "close");
    if (!real_close_range_fn) real_close_range_fn = dlsym(RTLD_NEXT, "close_range");
    if (!real_fcloseall_fn) real_fcloseall_fn = dlsym(RTLD_NEXT, "fcloseall");
    if (!real_read_fn) real_read_fn = dlsym(RTLD_NEXT, "read");
    if (!real_pread_fn) real_pread_fn = dlsym(RTLD_NEXT, "pread");
    if (!real_pread64_fn) real_pread64_fn = dlsym(RTLD_NEXT, "pread64");
    if (!real_readv_fn) real_readv_fn = dlsym(RTLD_NEXT, "readv");
    if (!real_preadv_fn) real_preadv_fn = dlsym(RTLD_NEXT, "preadv");
    if (!real_preadv2_fn) real_preadv2_fn = dlsym(RTLD_NEXT, "preadv2");
    if (!real_preadv64_fn) real_preadv64_fn = dlsym(RTLD_NEXT, "preadv64");
    if (!real_preadv64v2_fn) real_preadv64v2_fn = dlsym(RTLD_NEXT, "preadv64v2");
    if (!real___read_chk_fn) real___read_chk_fn = dlsym(RTLD_NEXT, "__read_chk");
    if (!real___pread_chk_fn) real___pread_chk_fn = dlsym(RTLD_NEXT, "__pread_chk");
    if (!real___pread64_chk_fn) real___pread64_chk_fn = dlsym(RTLD_NEXT, "__pread64_chk");
    if (!real_aio_read_fn) real_aio_read_fn = dlsym(RTLD_NEXT, "aio_read");
    if (!real_aio_read64_fn) real_aio_read64_fn = dlsym(RTLD_NEXT, "aio_read64");
    if (!real_lio_listio_fn) real_lio_listio_fn = dlsym(RTLD_NEXT, "lio_listio");
    if (!real_lio_listio64_fn) real_lio_listio64_fn = dlsym(RTLD_NEXT, "lio_listio64");
    if (!real_sendfile_fn) real_sendfile_fn = dlsym(RTLD_NEXT, "sendfile");
    if (!real_sendfile64_fn) real_sendfile64_fn = dlsym(RTLD_NEXT, "sendfile64");
    if (!real_copy_file_range_fn) real_copy_file_range_fn = dlsym(RTLD_NEXT, "copy_file_range");
    if (!real_splice_fn) real_splice_fn = dlsym(RTLD_NEXT, "splice");
    if (!real_sendmsg_fn) real_sendmsg_fn = dlsym(RTLD_NEXT, "sendmsg");
    if (!real_sendmmsg_fn) real_sendmmsg_fn = dlsym(RTLD_NEXT, "sendmmsg");
    if (!real_ioctl_fn) real_ioctl_fn = dlsym(RTLD_NEXT, "ioctl");
    if (!real_lseek_fn) real_lseek_fn = dlsym(RTLD_NEXT, "lseek");
    if (!real_lseek64_fn) real_lseek64_fn = dlsym(RTLD_NEXT, "lseek64");
    if (!real_dup_fn) real_dup_fn = dlsym(RTLD_NEXT, "dup");
    if (!real_dup2_fn) real_dup2_fn = dlsym(RTLD_NEXT, "dup2");
    if (!real_dup3_fn) real_dup3_fn = dlsym(RTLD_NEXT, "dup3");
    if (!real_fcntl_fn) real_fcntl_fn = dlsym(RTLD_NEXT, "fcntl");
    if (!real_fcntl64_fn) real_fcntl64_fn = dlsym(RTLD_NEXT, "fcntl64");
    if (!real_fork_fn) real_fork_fn = dlsym(RTLD_NEXT, "fork");
    if (!real_vfork_fn) real_vfork_fn = dlsym(RTLD_NEXT, "vfork");
    if (!real_clone_fn) real_clone_fn = dlsym(RTLD_NEXT, "clone");
    if (!real_posix_spawn_fn) real_posix_spawn_fn = dlsym(RTLD_NEXT, "posix_spawn");
    if (!real_posix_spawnp_fn) real_posix_spawnp_fn = dlsym(RTLD_NEXT, "posix_spawnp");
    if (!real_posix_spawn_file_actions_init_fn) {
        real_posix_spawn_file_actions_init_fn = dlsym(
            RTLD_NEXT,
            "posix_spawn_file_actions_init");
    }
    if (!real_posix_spawn_file_actions_destroy_fn) {
        real_posix_spawn_file_actions_destroy_fn = dlsym(
            RTLD_NEXT,
            "posix_spawn_file_actions_destroy");
    }
    if (!real_posix_spawn_file_actions_adddup2_fn) {
        real_posix_spawn_file_actions_adddup2_fn = dlsym(
            RTLD_NEXT,
            "posix_spawn_file_actions_adddup2");
    }
    if (!real_posix_spawn_file_actions_addopen_fn) {
        real_posix_spawn_file_actions_addopen_fn = dlsym(
            RTLD_NEXT,
            "posix_spawn_file_actions_addopen");
    }
    if (!real_posix_spawn_file_actions_addchdir_np_fn) {
        real_posix_spawn_file_actions_addchdir_np_fn = dlsym(
            RTLD_NEXT,
            "posix_spawn_file_actions_addchdir_np");
    }
    if (!real_posix_spawn_file_actions_addfchdir_np_fn) {
        real_posix_spawn_file_actions_addfchdir_np_fn = dlsym(
            RTLD_NEXT,
            "posix_spawn_file_actions_addfchdir_np");
    }
    if (!real_fopen_fn) real_fopen_fn = dlsym(RTLD_NEXT, "fopen");
    if (!real_fopen64_fn) real_fopen64_fn = dlsym(RTLD_NEXT, "fopen64");
    if (!real_freopen_fn) real_freopen_fn = dlsym(RTLD_NEXT, "freopen");
    if (!real_freopen64_fn) real_freopen64_fn = dlsym(RTLD_NEXT, "freopen64");
    if (!real_fdopen_fn) real_fdopen_fn = dlsym(RTLD_NEXT, "fdopen");
    if (!real_fclose_fn) real_fclose_fn = dlsym(RTLD_NEXT, "fclose");
    if (!real_setvbuf_fn) real_setvbuf_fn = dlsym(RTLD_NEXT, "setvbuf");
    if (!real_setbuf_fn) real_setbuf_fn = dlsym(RTLD_NEXT, "setbuf");
    if (!real_setbuffer_fn) real_setbuffer_fn = dlsym(RTLD_NEXT, "setbuffer");
    if (!real_setlinebuf_fn) real_setlinebuf_fn = dlsym(RTLD_NEXT, "setlinebuf");
    if (!real_fread_fn) real_fread_fn = dlsym(RTLD_NEXT, "fread");
    if (!real_fread_unlocked_fn) real_fread_unlocked_fn = dlsym(RTLD_NEXT, "fread_unlocked");
    if (!real___fread_chk_fn) real___fread_chk_fn = dlsym(RTLD_NEXT, "__fread_chk");
    if (!real___fread_unlocked_chk_fn) real___fread_unlocked_chk_fn = dlsym(RTLD_NEXT, "__fread_unlocked_chk");
    if (!real_fgetc_fn) real_fgetc_fn = dlsym(RTLD_NEXT, "fgetc");
    if (!real_getc_fn) real_getc_fn = dlsym(RTLD_NEXT, "getc");
    if (!real_fgetc_unlocked_fn) real_fgetc_unlocked_fn = dlsym(RTLD_NEXT, "fgetc_unlocked");
    if (!real_getc_unlocked_fn) real_getc_unlocked_fn = dlsym(RTLD_NEXT, "getc_unlocked");
    if (!real___uflow_fn) real___uflow_fn = dlsym(RTLD_NEXT, "__uflow");
    if (!real_fgets_fn) real_fgets_fn = dlsym(RTLD_NEXT, "fgets");
    if (!real_fgets_unlocked_fn) real_fgets_unlocked_fn = dlsym(RTLD_NEXT, "fgets_unlocked");
    if (!real_getdelim_fn) real_getdelim_fn = dlsym(RTLD_NEXT, "getdelim");
    if (!real___getdelim_fn) real___getdelim_fn = dlsym(RTLD_NEXT, "__getdelim");
    if (!real_vfscanf_fn) real_vfscanf_fn = dlsym(RTLD_NEXT, "vfscanf");
    if (!real___isoc99_vfscanf_fn) real___isoc99_vfscanf_fn = dlsym(RTLD_NEXT, "__isoc99_vfscanf");
    if (!real___isoc23_vfscanf_fn) real___isoc23_vfscanf_fn = dlsym(RTLD_NEXT, "__isoc23_vfscanf");
    if (!real_fgetwc_fn) real_fgetwc_fn = dlsym(RTLD_NEXT, "fgetwc");
    if (!real_getwc_fn) real_getwc_fn = dlsym(RTLD_NEXT, "getwc");
    if (!real_fgetwc_unlocked_fn) real_fgetwc_unlocked_fn = dlsym(RTLD_NEXT, "fgetwc_unlocked");
    if (!real_getwc_unlocked_fn) real_getwc_unlocked_fn = dlsym(RTLD_NEXT, "getwc_unlocked");
    if (!real_fgetws_fn) real_fgetws_fn = dlsym(RTLD_NEXT, "fgetws");
    if (!real_fgetws_unlocked_fn) real_fgetws_unlocked_fn = dlsym(RTLD_NEXT, "fgetws_unlocked");
    if (!real___fgetws_chk_fn) real___fgetws_chk_fn = dlsym(RTLD_NEXT, "__fgetws_chk");
    if (!real___fgetws_unlocked_chk_fn) {
        real___fgetws_unlocked_chk_fn = dlsym(RTLD_NEXT, "__fgetws_unlocked_chk");
    }
    if (!real_vfwscanf_fn) real_vfwscanf_fn = dlsym(RTLD_NEXT, "vfwscanf");
    if (!real___isoc99_vfwscanf_fn) real___isoc99_vfwscanf_fn = dlsym(RTLD_NEXT, "__isoc99_vfwscanf");
    if (!real___isoc23_vfwscanf_fn) real___isoc23_vfwscanf_fn = dlsym(RTLD_NEXT, "__isoc23_vfwscanf");
    if (!real_mmap_fn) real_mmap_fn = dlsym(RTLD_NEXT, "mmap");
    if (!real_mmap64_fn) real_mmap64_fn = dlsym(RTLD_NEXT, "mmap64");
    if (!real_faccessat_fn) real_faccessat_fn = dlsym(RTLD_NEXT, "faccessat");
    if (!real_statx_fn) real_statx_fn = dlsym(RTLD_NEXT, "statx");
}

static int vfs_log_enabled(void) {
    if (log_state == -1) {
        const char *value = getenv("VFS_LOG");
        log_state = value && *value && strcmp(value, "0") != 0;
    }
    return log_state;
}

static void vfs_log(const char *format, ...) {
    if (!vfs_log_enabled()) return;
    va_list arguments;
    va_start(arguments, format);
    fprintf(stderr, "[model-vfs] ");
    vfprintf(stderr, format, arguments);
    fprintf(stderr, "\n");
    va_end(arguments);
}

static uint64_t read_u64_be(const uint8_t *buffer) {
    uint64_t value;
    memcpy(&value, buffer, sizeof(value));
    return be64toh(value);
}

static void write_u64_be(uint8_t *buffer, uint64_t value) {
    value = htobe64(value);
    memcpy(buffer, &value, sizeof(value));
}

static void write_u32_be(uint8_t *buffer, uint32_t value) {
    value = htonl(value);
    memcpy(buffer, &value, sizeof(value));
}

static int write_exact(int fd, const void *buffer, size_t length) {
    const uint8_t *current = buffer;
    size_t written = 0;
    while (written < length) {
        ssize_t result = syscall(SYS_write, fd, current + written, length - written);
        if (result < 0 && errno == EINTR) continue;
        if (result <= 0) return -1;
        written += (size_t)result;
    }
    return 0;
}

static int read_exact(int fd, void *buffer, size_t length) {
    uint8_t *current = buffer;
    size_t read_total = 0;
    while (read_total < length) {
        ssize_t result = syscall(SYS_read, fd, current + read_total, length - read_total);
        if (result < 0 && errno == EINTR) continue;
        if (result <= 0) return -1;
        read_total += (size_t)result;
    }
    return 0;
}

static int ipc_roundtrip(
    uint8_t operation,
    const uint8_t *payload,
    uint32_t payload_length,
    ipc_response_t *response) {
    const char *socket_path = getenv("VFS_SOCKET");
    if (!socket_path || !*socket_path || payload_length > VFS_MAX_PAYLOAD) return -1;

    int socket_fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (socket_fd < 0) return -1;

    struct sockaddr_un address;
    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    if (strlen(socket_path) >= sizeof(address.sun_path)) {
        syscall(SYS_close, socket_fd);
        return -1;
    }
    snprintf(address.sun_path, sizeof(address.sun_path), "%s", socket_path);

    if (connect(socket_fd, (struct sockaddr *)&address, sizeof(address)) != 0) {
        syscall(SYS_close, socket_fd);
        return -1;
    }

    uint8_t header[VFS_HEADER_LENGTH] = {0};
    write_u32_be(header, VFS_MAGIC);
    header[4] = VFS_VERSION;
    header[5] = operation;
    write_u32_be(header + 8, payload_length);
    if (write_exact(socket_fd, header, sizeof(header)) != 0 ||
        (payload_length > 0 && write_exact(socket_fd, payload, payload_length) != 0)) {
        syscall(SYS_close, socket_fd);
        return -1;
    }

    uint8_t response_buffer[VFS_RESPONSE_LENGTH];
    if (read_exact(socket_fd, response_buffer, sizeof(response_buffer)) != 0) {
        syscall(SYS_close, socket_fd);
        return -1;
    }
    syscall(SYS_close, socket_fd);

    uint32_t magic;
    memcpy(&magic, response_buffer, sizeof(magic));
    if (ntohl(magic) != VFS_MAGIC || response_buffer[4] != VFS_VERSION) return -1;

    response->status = response_buffer[5];
    response->disposition = response_buffer[6];
    response->lease_id = read_u64_be(response_buffer + 12);
    response->epoch = (int64_t)read_u64_be(response_buffer + 20);
    response->expected_length = (int64_t)read_u64_be(response_buffer + 28);
    response->device_id = read_u64_be(response_buffer + 36);
    response->inode = read_u64_be(response_buffer + 44);
    return 0;
}

static int request_open(const char *path, int write_access, ipc_response_t *response) {
    const char *session_id = getenv("VFS_SESSION_ID");
    if (!session_id) session_id = "";
    size_t path_length = strlen(path);
    size_t session_length = strlen(session_id);
    if (path_length == 0 || path_length + session_length + 9 > VFS_MAX_PAYLOAD) return -1;

    uint32_t payload_length = (uint32_t)(9 + path_length + session_length);
    uint8_t *payload = malloc(payload_length);
    if (!payload) return -1;
    write_u32_be(payload, (uint32_t)path_length);
    write_u32_be(payload + 4, (uint32_t)session_length);
    payload[8] = write_access ? 1 : 0;
    memcpy(payload + 9, path, path_length);
    memcpy(payload + 9 + path_length, session_id, session_length);
    int result = ipc_roundtrip(VFS_OP_OPEN, payload, payload_length, response);
    free(payload);
    if (result == 0 &&
        response->status == VFS_STATUS_SUCCESS &&
        response->disposition == VFS_OPEN_RANGE_MANAGED) {
        uint8_t acknowledgement_payload[8];
        ipc_response_t acknowledgement_response = {0};
        write_u64_be(acknowledgement_payload, response->lease_id);
        if (ipc_roundtrip(
                VFS_OP_ACKNOWLEDGE_OPEN,
                acknowledgement_payload,
                sizeof(acknowledgement_payload),
                &acknowledgement_response) != 0 ||
            acknowledgement_response.status != VFS_STATUS_SUCCESS) {
            request_release(response->lease_id);
            return -1;
        }
    }
    return result;
}

static int request_range(
    uint64_t lease_id,
    int64_t epoch,
    uint64_t offset,
    uint64_t length,
    ipc_response_t *response) {
    uint8_t payload[32];
    write_u64_be(payload, lease_id);
    write_u64_be(payload + 8, (uint64_t)epoch);
    write_u64_be(payload + 16, offset);
    write_u64_be(payload + 24, length);
    return ipc_roundtrip(VFS_OP_ENSURE_RANGE, payload, sizeof(payload), response);
}

static int request_complete(
    uint64_t lease_id,
    int64_t epoch,
    const char *reason,
    ipc_response_t *response) {
    size_t reason_length = reason ? strlen(reason) : 0;
    if (reason_length + 20 > VFS_MAX_PAYLOAD) return -1;
    uint32_t payload_length = (uint32_t)(20 + reason_length);
    uint8_t *payload = malloc(payload_length);
    if (!payload) return -1;
    write_u64_be(payload, lease_id);
    write_u64_be(payload + 8, (uint64_t)epoch);
    write_u32_be(payload + 16, (uint32_t)reason_length);
    if (reason_length > 0) memcpy(payload + 20, reason, reason_length);
    int result = ipc_roundtrip(VFS_OP_ENSURE_COMPLETE, payload, payload_length, response);
    free(payload);
    return result;
}

static int send_release_once(uint64_t lease_id) {
    uint8_t payload[8];
    ipc_response_t response = {0};
    write_u64_be(payload, lease_id);
    reentry++;
    int result = ipc_roundtrip(VFS_OP_RELEASE, payload, sizeof(payload), &response);
    reentry--;
    return result == 0 && response.status == VFS_STATUS_SUCCESS ? 0 : -1;
}

static void *pending_release_worker(void *unused) {
    (void)unused;
    for (;;) {
        pthread_mutex_lock(&pending_release_lock);
        while (!pending_releases) {
            pthread_cond_wait(&pending_release_ready, &pending_release_lock);
        }
        uint64_t lease_id = pending_releases->lease_id;
        pthread_mutex_unlock(&pending_release_lock);

        if (send_release_once(lease_id) == 0) {
            pthread_mutex_lock(&pending_release_lock);
            pending_release_t **position = &pending_releases;
            while (*position && (*position)->lease_id != lease_id) {
                position = &(*position)->next;
            }
            if (*position) {
                pending_release_t *released = *position;
                *position = released->next;
                free(released);
            }
            pthread_mutex_unlock(&pending_release_lock);
            continue;
        }

        usleep(100000);
    }

    return NULL;
}

static void start_pending_release_worker_locked(void) {
    if (pending_release_worker_started) return;
    pthread_t worker;
    if (pthread_create(&worker, NULL, pending_release_worker, NULL) == 0) {
        pthread_detach(worker);
        pending_release_worker_started = 1;
    }
}

static void retain_failed_release(uint64_t lease_id) {
    pending_release_t *pending = calloc(1, sizeof(*pending));
    if (!pending) return;
    pending->lease_id = lease_id;

    pthread_mutex_lock(&pending_release_lock);
    pending_release_t **position = &pending_releases;
    while (*position) {
        if ((*position)->lease_id == lease_id) {
            start_pending_release_worker_locked();
            pthread_cond_signal(&pending_release_ready);
            pthread_mutex_unlock(&pending_release_lock);
            free(pending);
            return;
        }
        position = &(*position)->next;
    }
    *position = pending;

    start_pending_release_worker_locked();
    pthread_cond_signal(&pending_release_ready);
    pthread_mutex_unlock(&pending_release_lock);
}

static void request_release(uint64_t lease_id) {
    if (send_release_once(lease_id) != 0) {
        retain_failed_release(lease_id);
    }
}

static int starts_with(const char *value, const char *prefix) {
    size_t length = strlen(prefix);
    if (length == 1 && prefix[0] == '/') return value[0] == '/';
    return strncmp(value, prefix, length) == 0 &&
           (value[length] == '/' || value[length] == '\0');
}

static int normalize_absolute_path(const char *input, char *output, size_t output_size) {
    if (!input || input[0] != '/' || !output || output_size < 2) return -1;
    char copy[PATH_MAX];
    if (snprintf(copy, sizeof(copy), "%s", input) >= (int)sizeof(copy)) return -1;

    size_t output_length = 1;
    output[0] = '/';
    output[1] = '\0';
    char *save = NULL;
    for (char *segment = strtok_r(copy, "/", &save);
         segment;
         segment = strtok_r(NULL, "/", &save)) {
        if (strcmp(segment, ".") == 0 || *segment == '\0') continue;
        if (strcmp(segment, "..") == 0) {
            if (output_length > 1) {
                while (output_length > 1 && output[output_length - 1] != '/') output_length--;
                if (output_length > 1) output_length--;
                output[output_length] = '\0';
            }
            continue;
        }

        size_t segment_length = strlen(segment);
        size_t separator_length = output_length > 1 ? 1 : 0;
        if (output_length + separator_length + segment_length >= output_size) return -1;
        if (separator_length) output[output_length++] = '/';
        memcpy(output + output_length, segment, segment_length);
        output_length += segment_length;
        output[output_length] = '\0';
    }
    return 0;
}

static int has_allowed_extension(const char *path) {
    const char *extensions = getenv("VFS_EXTS");
    if (!extensions || !*extensions) extensions = ".safetensors,.pt,.bin,.ckpt";
    const char *dot = strrchr(path, '.');
    if (!dot) return 0;

    size_t dot_length = strlen(dot);
    const char *current = extensions;
    while (*current) {
        while (*current == ' ' || *current == ',') current++;
        const char *start = current;
        while (*current && *current != ',') current++;
        size_t token_length = (size_t)(current - start);
        if (token_length == dot_length && strncasecmp(start, dot, token_length) == 0) return 1;
    }
    return 0;
}

static int under_watched_root(const char *absolute_path) {
    const char *roots = getenv("VFS_ROOTS");
    if (!roots || !*roots) return 0;
    char buffer[8192];
    snprintf(buffer, sizeof(buffer), "%s", roots);
    char *save = NULL;
    for (char *root = strtok_r(buffer, ":", &save);
         root;
         root = strtok_r(NULL, ":", &save)) {
        char normalized_root[PATH_MAX];
        if (normalize_absolute_path(root, normalized_root, sizeof(normalized_root)) == 0 &&
            starts_with(absolute_path, normalized_root)) return 1;
    }
    return 0;
}

static int absolute_path_from(int directory_fd, const char *path, char *output, size_t output_size) {
    if (!path || !output || output_size == 0) return -1;
    char combined[PATH_MAX];
    if (path[0] == '/') {
        if (snprintf(combined, sizeof(combined), "%s", path) >= (int)sizeof(combined)) return -1;
        return normalize_absolute_path(combined, output, output_size);
    }

    if (directory_fd == AT_FDCWD) {
        char current_directory[PATH_MAX];
        if (!getcwd(current_directory, sizeof(current_directory))) return -1;
        if (snprintf(combined, sizeof(combined), "%s/%s", current_directory, path) >=
            (int)sizeof(combined)) return -1;
        return normalize_absolute_path(combined, output, output_size);
    }

    char link_path[64];
    char directory_path[PATH_MAX];
    snprintf(link_path, sizeof(link_path), "/proc/self/fd/%d", directory_fd);
    ssize_t length = readlink(link_path, directory_path, sizeof(directory_path) - 1);
    if (length <= 0) return -1;
    directory_path[length] = '\0';
    if (snprintf(combined, sizeof(combined), "%s/%s", directory_path, path) >=
        (int)sizeof(combined)) return -1;
    return normalize_absolute_path(combined, output, output_size);
}

static int has_numeric_suffix(const char *path, const char *prefix) {
    size_t prefix_length = strlen(prefix);
    if (strncmp(path, prefix, prefix_length) != 0) return 0;
    const char *value = path + prefix_length;
    if (!*value) return 0;
    for (; *value; value++) {
        if (*value < '0' || *value > '9') return 0;
    }
    return 1;
}

static int is_descriptor_alias_path(const char *path) {
    if (has_numeric_suffix(path, "/proc/self/fd/") ||
        has_numeric_suffix(path, "/proc/thread-self/fd/") ||
        has_numeric_suffix(path, "/dev/fd/")) return 1;

    if (strncmp(path, "/proc/", 6) != 0) return 0;
    const char *cursor = path + 6;
    if (*cursor < '0' || *cursor > '9') return 0;
    while (*cursor >= '0' && *cursor <= '9') cursor++;
    if (strncmp(cursor, "/fd/", 4) == 0) {
        cursor += 4;
    } else if (strncmp(cursor, "/task/", 6) == 0) {
        cursor += 6;
        if (*cursor < '0' || *cursor > '9') return 0;
        while (*cursor >= '0' && *cursor <= '9') cursor++;
        if (strncmp(cursor, "/fd/", 4) != 0) return 0;
        cursor += 4;
    } else {
        return 0;
    }

    if (*cursor < '0' || *cursor > '9') return 0;
    while (*cursor >= '0' && *cursor <= '9') cursor++;
    return *cursor == '\0';
}

static int resolve_descriptor_alias_path(
    const char *alias_path,
    char *output,
    size_t output_size) {
    if (!is_descriptor_alias_path(alias_path)) return -1;

    char target[PATH_MAX];
    ssize_t length = readlink(alias_path, target, sizeof(target) - 1);
    if (length <= 0 || length >= (ssize_t)sizeof(target)) return -1;
    target[length] = '\0';

    if (target[0] == '/') {
        return normalize_absolute_path(target, output, output_size);
    }

    char alias_directory[PATH_MAX];
    if (snprintf(alias_directory, sizeof(alias_directory), "%s", alias_path) >=
        (int)sizeof(alias_directory)) return -1;
    char *separator = strrchr(alias_directory, '/');
    if (!separator) return -1;
    *separator = '\0';

    char combined[PATH_MAX];
    if (snprintf(combined, sizeof(combined), "%s/%s", alias_directory, target) >=
        (int)sizeof(combined)) return -1;
    return normalize_absolute_path(combined, output, output_size);
}

static int resolve_path_alias(
    const char *absolute_path,
    char *output,
    size_t output_size) {
    if (resolve_descriptor_alias_path(absolute_path, output, output_size) == 0) return 0;

    char resolved[PATH_MAX];
    if (realpath(absolute_path, resolved)) {
        return normalize_absolute_path(resolved, output, output_size);
    }

    // A create-style open may name a missing final component beneath a
    // symlinked parent. Canonicalize the existing parent so root filtering is
    // still based on the kernel destination rather than the lexical path.
    char parent[PATH_MAX];
    if (snprintf(parent, sizeof(parent), "%s", absolute_path) >= (int)sizeof(parent)) return -1;
    char *separator = strrchr(parent, '/');
    if (!separator || !separator[1]) return -1;
    char name[NAME_MAX + 1];
    if (snprintf(name, sizeof(name), "%s", separator + 1) >= (int)sizeof(name)) return -1;
    if (separator == parent) {
        separator[1] = '\0';
    } else {
        *separator = '\0';
    }

    char resolved_parent[PATH_MAX];
    if (!realpath(parent, resolved_parent)) return -1;
    char combined[PATH_MAX];
    if (snprintf(combined, sizeof(combined), "%s/%s", resolved_parent, name) >=
        (int)sizeof(combined)) return -1;
    return normalize_absolute_path(combined, output, output_size);
}

static int path_requires_vfs_handling(const char *path) {
    char absolute_path[PATH_MAX];
    if (absolute_path_from(AT_FDCWD, path, absolute_path, sizeof(absolute_path)) != 0) return 0;
    char resolved_path[PATH_MAX];
    if (resolve_path_alias(absolute_path, resolved_path, sizeof(resolved_path)) == 0) {
        if (snprintf(absolute_path, sizeof(absolute_path), "%s", resolved_path) >=
            (int)sizeof(absolute_path)) return 0;
    }
    return under_watched_root(absolute_path) && has_allowed_extension(absolute_path);
}

static void clear_intervals(shared_state_t *state) {
    interval_t *current = state->readable;
    while (current) {
        interval_t *next = current->next;
        free(current);
        current = next;
    }
    state->readable = NULL;
}

static int interval_contains(shared_state_t *state, uint64_t start, uint64_t end) {
    if (state->complete || start >= end) return 1;
    for (interval_t *current = state->readable; current; current = current->next) {
        if (current->start <= start && current->end >= end) return 1;
    }
    return 0;
}

static void add_interval(shared_state_t *state, uint64_t start, uint64_t end) {
    if (start >= end || interval_contains(state, start, end)) return;
    interval_t **position = &state->readable;
    while (*position && (*position)->end < start) position = &(*position)->next;

    if (*position && (*position)->start <= end) {
        interval_t *current = *position;
        if (current->start > start) current->start = start;
        if (current->end < end) current->end = end;
        while (current->next && current->next->start <= current->end) {
            interval_t *merged = current->next;
            if (merged->end > current->end) current->end = merged->end;
            current->next = merged->next;
            free(merged);
        }
        return;
    }

    interval_t *created = calloc(1, sizeof(*created));
    if (!created) return;
    created->start = start;
    created->end = end;
    created->next = *position;
    *position = created;
}

static shared_state_t *create_state(const ipc_response_t *response) {
    shared_state_t *state = calloc(1, sizeof(*state));
    if (!state) return NULL;
    if (pthread_mutex_init(&state->io_lock, NULL) != 0) {
        free(state);
        return NULL;
    }
    state->references = 1;
    state->lease_id = response->lease_id;
    state->epoch = response->epoch;
    state->expected_length = response->expected_length;
    return state;
}

static void state_add_reference(shared_state_t *state) {
    __sync_add_and_fetch(&state->references, 1);
}

static void state_release_reference(shared_state_t *state) {
    if (!state || __sync_sub_and_fetch(&state->references, 1) != 0) return;
    request_release(state->lease_id);
    clear_intervals(state);
    pthread_mutex_destroy(&state->io_lock);
    free(state);
}

static int attach_fd_locked(int fd, shared_state_t *state, int add_reference) {
    if (fd < 0 || !state) return -1;
    fd_entry_t *entry = calloc(1, sizeof(*entry));
    if (!entry) return -1;
    entry->fd = fd;
    entry->state = state;
    if (add_reference) state_add_reference(state);
    entry->next = fd_entries;
    fd_entries = entry;
    return 0;
}

static int attach_fd(int fd, shared_state_t *state, int add_reference) {
    pthread_mutex_lock(&fd_map_lock);
    int result = attach_fd_locked(fd, state, add_reference);
    pthread_mutex_unlock(&fd_map_lock);
    return result;
}

static shared_state_t *acquire_fd_state(int fd) {
    shared_state_t *state = NULL;
    pthread_mutex_lock(&fd_map_lock);
    for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
        if (entry->fd == fd) {
            state = entry->state;
            state_add_reference(state);
            break;
        }
    }
    pthread_mutex_unlock(&fd_map_lock);
    return state;
}

static shared_state_t *acquire_fd_state_for_io(int fd) {
    shared_state_t *state = NULL;
    pthread_mutex_lock(&fd_map_lock);
    for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
        if (entry->fd == fd) {
            state = entry->state;
            state_add_reference(state);
            // Lock the open-file-description state before publishing the map
            // lock. Descriptor replacement takes these locks in the same order,
            // so a read cannot carry stale bookkeeping across dup2/dup3.
            pthread_mutex_lock(&state->io_lock);
            break;
        }
    }
    pthread_mutex_unlock(&fd_map_lock);
    return state;
}

static shared_state_t *acquire_stream_state_for_io(FILE *file) {
    // Establish one lock order for every stdio path, including callers that
    // already hold flockfile: stream -> descriptor map -> shared state.
    flockfile(file);
    shared_state_t *state = acquire_fd_state_for_io(fileno(file));
    if (!state) funlockfile(file);
    return state;
}

static void release_stream_state_after_io(FILE *file, shared_state_t *state) {
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    funlockfile(file);
}

static shared_state_t *lock_and_detach_fd_for_close(int fd) {
    shared_state_t *state = NULL;
    // Match the map -> open-file-description lock order used by reads and
    // descriptor replacement. Keep both locks until the kernel close returns
    // so the numeric fd cannot be reused underneath an in-flight managed read.
    pthread_mutex_lock(&fd_map_lock);
    fd_entry_t **position = &fd_entries;
    while (*position) {
        if ((*position)->fd == fd) {
            fd_entry_t *removed = *position;
            state = removed->state;
            pthread_mutex_lock(&state->io_lock);
            *position = removed->next;
            free(removed);
            break;
        }
        position = &(*position)->next;
    }
    return state;
}

static void unlock_after_close(shared_state_t *state) {
    if (state) pthread_mutex_unlock(&state->io_lock);
    pthread_mutex_unlock(&fd_map_lock);
    if (state) state_release_reference(state);
}

static int ensure_range_locked(shared_state_t *state, uint64_t offset, uint64_t length) {
    if (length == 0 || offset >= (uint64_t)state->expected_length) return 0;
    uint64_t end = offset > UINT64_MAX - length ? UINT64_MAX : offset + length;
    if (end > (uint64_t)state->expected_length) end = (uint64_t)state->expected_length;
    if (interval_contains(state, offset, end)) return 0;

    ipc_response_t response;
    reentry++;
    int result = request_range(state->lease_id, state->epoch, offset, end - offset, &response);
    reentry--;
    if (result != 0 || response.status != VFS_STATUS_SUCCESS) {
        errno = EIO;
        return -1;
    }

    if (response.epoch != state->epoch) {
        state->epoch = response.epoch;
        state->complete = 0;
        clear_intervals(state);
    }
    add_interval(state, offset, end);
    return 0;
}

static int ensure_complete_locked(shared_state_t *state, const char *reason) {
    if (state->complete) return 0;
    ipc_response_t response;
    reentry++;
    int result = request_complete(state->lease_id, state->epoch, reason, &response);
    reentry--;
    if (result != 0 || response.status != VFS_STATUS_SUCCESS) {
        errno = EIO;
        return -1;
    }
    if (response.epoch != state->epoch) {
        state->epoch = response.epoch;
        clear_intervals(state);
    }
    state->complete = 1;
    clear_intervals(state);
    return 0;
}

static int prepare_open(
    int directory_fd,
    const char *path,
    int write_access,
    ipc_response_t *response,
    char *absolute_path,
    size_t absolute_path_size) {
    if (!path || reentry || (getenv("VFS_SHIM_DISABLED") && *getenv("VFS_SHIM_DISABLED"))) return 0;
    if (absolute_path_from(directory_fd, path, absolute_path, absolute_path_size) != 0) return 0;
    char resolved_path[PATH_MAX];
    if (resolve_path_alias(absolute_path, resolved_path, sizeof(resolved_path)) == 0) {
        if (snprintf(absolute_path, absolute_path_size, "%s", resolved_path) >=
            (int)absolute_path_size) return 0;
    }
    if (!under_watched_root(absolute_path) || !has_allowed_extension(absolute_path)) return 0;

    vfs_log("open path=%s write=%d", absolute_path, write_access);
    reentry++;
    int result = request_open(absolute_path, write_access, response);
    reentry--;
    if (result != 0) {
        errno = EIO;
        return -1;
    }
    if (response->status == VFS_STATUS_NOT_MANAGED) return 0;
    if (response->status != VFS_STATUS_SUCCESS) {
        errno = response->status == VFS_STATUS_READ_ONLY ? EROFS : EIO;
        return -1;
    }
    if (response->disposition == VFS_OPEN_RANGE_MANAGED && write_access) {
        request_release(response->lease_id);
        errno = EROFS;
        return -1;
    }
    vfs_log(
        "open response path=%s disposition=%u lease=%llu epoch=%lld",
        absolute_path,
        (unsigned int)response->disposition,
        (unsigned long long)response->lease_id,
        (long long)response->epoch);
    return response->disposition == VFS_OPEN_RANGE_MANAGED ? 2 : 1;
}

static int force_descriptor_cloexec(int fd) {
    int descriptor_flags = real_fcntl_fn(fd, F_GETFD);
    if (descriptor_flags < 0) return -1;
    return real_fcntl_fn(fd, F_SETFD, descriptor_flags | FD_CLOEXEC);
}

static shared_state_t *create_opened_fd_state(
    int fd,
    const ipc_response_t *response,
    int close_on_failure) {
    if (fd < 0) {
        int saved_errno = errno;
        request_release(response->lease_id);
        errno = saved_errno;
        return NULL;
    }
    struct stat stat_buffer;
    if (fstat(fd, &stat_buffer) != 0) {
        request_release(response->lease_id);
        if (close_on_failure) real_close_fn(fd);
        errno = EIO;
        return NULL;
    }
    uint64_t device_id = ((uint64_t)major(stat_buffer.st_dev) << 32) |
                         (uint64_t)minor(stat_buffer.st_dev);
    if (stat_buffer.st_size != response->expected_length ||
        device_id != response->device_id ||
        (uint64_t)stat_buffer.st_ino != response->inode) {
        request_release(response->lease_id);
        if (close_on_failure) real_close_fn(fd);
        errno = EIO;
        return NULL;
    }

    if (force_descriptor_cloexec(fd) < 0) {
        request_release(response->lease_id);
        if (close_on_failure) real_close_fn(fd);
        errno = EIO;
        return NULL;
    }

    shared_state_t *state = create_state(response);
    if (!state) {
        request_release(response->lease_id);
        if (close_on_failure) real_close_fn(fd);
        errno = ENOMEM;
        return NULL;
    }
    return state;
}

static int attach_opened_fd(int fd, const ipc_response_t *response, int close_on_failure) {
    if (response->disposition != VFS_OPEN_RANGE_MANAGED) return fd;
    shared_state_t *state = create_opened_fd_state(fd, response, close_on_failure);
    if (!state) return -1;
    if (attach_fd(fd, state, 0) != 0) {
        state_release_reference(state);
        if (close_on_failure) real_close_fn(fd);
        errno = ENOMEM;
        return -1;
    }
    managed_descriptor_seen = 1;
    return fd;
}

static int mode_is_read_only(const char *mode) {
    return mode && mode[0] == 'r' && !strchr(mode, '+');
}

static size_t iov_length(const struct iovec *iov, int count) {
    size_t total = 0;
    if (!iov || count <= 0) return 0;
    for (int index = 0; index < count; index++) {
        if (iov[index].iov_len > SIZE_MAX - total) return SIZE_MAX;
        total += iov[index].iov_len;
    }
    return total;
}

static int open_flags_require_mode(int flags) {
    return (flags & O_CREAT) != 0 || (flags & O_TMPFILE) == O_TMPFILE;
}

int open(const char *pathname, int flags, ...) {
    mode_t mode = 0;
    if (open_flags_require_mode(flags)) {
        va_list arguments;
        va_start(arguments, flags);
        mode = (mode_t)va_arg(arguments, int);
        va_end(arguments);
    }
    return openat(AT_FDCWD, pathname, flags, mode);
}

int open64(const char *pathname, int flags, ...) {
    mode_t mode = 0;
    if (open_flags_require_mode(flags)) {
        va_list arguments;
        va_start(arguments, flags);
        mode = (mode_t)va_arg(arguments, int);
        va_end(arguments);
    }
    return openat64(AT_FDCWD, pathname, flags, mode);
}

int creat(const char *pathname, mode_t mode) {
    return open(pathname, O_WRONLY | O_CREAT | O_TRUNC, mode);
}

int creat64(const char *pathname, mode_t mode) {
    return open64(pathname, O_WRONLY | O_CREAT | O_TRUNC, mode);
}

int openat(int directory_fd, const char *pathname, int flags, ...) {
    init_syms();
    mode_t mode = 0;
    if (open_flags_require_mode(flags)) {
        va_list arguments;
        va_start(arguments, flags);
        mode = (mode_t)va_arg(arguments, int);
        va_end(arguments);
    }
    if (reentry) return real_openat_fn(directory_fd, pathname, flags, mode);

    ipc_response_t response = {0};
    char absolute_path[PATH_MAX];
    int write_access = flags & (O_CREAT | O_WRONLY | O_RDWR | O_TRUNC);
    if ((flags & O_TMPFILE) == O_TMPFILE) write_access = 1;
    int disposition = prepare_open(
        directory_fd,
        pathname,
        write_access,
        &response,
        absolute_path,
        sizeof(absolute_path));
    if (disposition < 0) return -1;
    if (disposition != 2) return real_openat_fn(directory_fd, pathname, flags, mode);
    pthread_mutex_lock(&inheritance_lock);
    int fd = real_openat_fn(directory_fd, pathname, flags, mode);
    int result = attach_opened_fd(fd, &response, 1);
    pthread_mutex_unlock(&inheritance_lock);
    return result;
}

int openat64(int directory_fd, const char *pathname, int flags, ...) {
    mode_t mode = 0;
    if (open_flags_require_mode(flags)) {
        va_list arguments;
        va_start(arguments, flags);
        mode = (mode_t)va_arg(arguments, int);
        va_end(arguments);
    }
    return openat(directory_fd, pathname, flags, mode);
}

int __open_2(const char *pathname, int flags) {
    return open(pathname, flags);
}

int __open64_2(const char *pathname, int flags) {
    return open64(pathname, flags);
}

int __openat_2(int directory_fd, const char *pathname, int flags) {
    return openat(directory_fd, pathname, flags);
}

int __openat64_2(int directory_fd, const char *pathname, int flags) {
    return openat64(directory_fd, pathname, flags);
}

FILE *fopen(const char *pathname, const char *mode) {
    init_syms();
    if (reentry) return real_fopen_fn(pathname, mode);
    ipc_response_t response = {0};
    char absolute_path[PATH_MAX];
    int disposition = prepare_open(
        AT_FDCWD,
        pathname,
        !mode_is_read_only(mode),
        &response,
        absolute_path,
        sizeof(absolute_path));
    if (disposition < 0) return NULL;
    if (disposition == 2) pthread_mutex_lock(&inheritance_lock);
    reentry++;
    FILE *file = real_fopen_fn(pathname, mode);
    reentry--;
    if (disposition != 2) return file;
    if (!file) {
        int saved_errno = errno;
        request_release(response.lease_id);
        pthread_mutex_unlock(&inheritance_lock);
        errno = saved_errno;
        return NULL;
    }
    if (attach_opened_fd(fileno(file), &response, 0) < 0) {
        int saved_errno = errno;
        real_fclose_fn(file);
        pthread_mutex_unlock(&inheritance_lock);
        errno = saved_errno;
        return NULL;
    }
    pthread_mutex_unlock(&inheritance_lock);
    setvbuf(file, NULL, _IONBF, 0);
    return file;
}

FILE *fopen64(const char *pathname, const char *mode) {
    init_syms();
    if (!real_fopen64_fn) return fopen(pathname, mode);
    if (reentry) return real_fopen64_fn(pathname, mode);
    ipc_response_t response = {0};
    char absolute_path[PATH_MAX];
    int disposition = prepare_open(
        AT_FDCWD,
        pathname,
        !mode_is_read_only(mode),
        &response,
        absolute_path,
        sizeof(absolute_path));
    if (disposition < 0) return NULL;
    if (disposition == 2) pthread_mutex_lock(&inheritance_lock);
    reentry++;
    FILE *file = real_fopen64_fn(pathname, mode);
    reentry--;
    if (disposition != 2) return file;
    if (!file) {
        int saved_errno = errno;
        request_release(response.lease_id);
        pthread_mutex_unlock(&inheritance_lock);
        errno = saved_errno;
        return NULL;
    }
    if (attach_opened_fd(fileno(file), &response, 0) < 0) {
        int saved_errno = errno;
        real_fclose_fn(file);
        pthread_mutex_unlock(&inheritance_lock);
        errno = saved_errno;
        return NULL;
    }
    pthread_mutex_unlock(&inheritance_lock);
    setvbuf(file, NULL, _IONBF, 0);
    return file;
}

static FILE *freopen_managed(
    const char *pathname,
    const char *mode,
    FILE *file,
    FILE *(*real_function)(const char *, const char *, FILE *)) {
    init_syms();
    if (!real_function) real_function = real_freopen_fn;
    int old_fd = fileno(file);
    ipc_response_t response = {0};
    char absolute_path[PATH_MAX];
    int disposition = pathname
        ? prepare_open(
              AT_FDCWD,
              pathname,
              !mode_is_read_only(mode),
              &response,
              absolute_path,
              sizeof(absolute_path))
        : 0;
    if (disposition < 0) return NULL;
    pthread_mutex_lock(&inheritance_lock);
    flockfile(file);

    // freopen replaces the descriptor underneath the FILE. Serialize that
    // replacement with managed I/O and update the descriptor map before any
    // blocked reader can observe the replacement.
    pthread_mutex_lock(&fd_map_lock);
    fd_entry_t **old_position = &fd_entries;
    while (*old_position && (*old_position)->fd != old_fd) {
        old_position = &(*old_position)->next;
    }
    fd_entry_t *old_entry = *old_position;
    shared_state_t *old_state = old_entry ? old_entry->state : NULL;
    if (!pathname && old_state && !mode_is_read_only(mode)) {
        pthread_mutex_unlock(&fd_map_lock);
        funlockfile(file);
        pthread_mutex_unlock(&inheritance_lock);
        errno = EROFS;
        return NULL;
    }
    if (old_state) pthread_mutex_lock(&old_state->io_lock);
    reentry++;
    FILE *result = real_function(pathname, mode, file);
    reentry--;
    int result_errno = errno;
    int preserve_old_state = !pathname && result && old_entry;
    shared_state_t *replacement_state = NULL;
    int replacement_state_attached = 0;
    int replacement_lease_consumed = 0;

    if (disposition == 2 && result) {
        replacement_state = create_opened_fd_state(fileno(result), &response, 0);
        replacement_lease_consumed = 1;
        if (!replacement_state) {
            result_errno = errno;
            real_fclose_fn(result);
            result = NULL;
        }
    }

    if (old_entry && !preserve_old_state) {
        *old_position = old_entry->next;
        free(old_entry);
    }
    else if (preserve_old_state) {
        old_entry->fd = fileno(result);
        if (force_descriptor_cloexec(old_entry->fd) < 0) {
            result_errno = errno;
            *old_position = old_entry->next;
            free(old_entry);
            preserve_old_state = 0;
            real_fclose_fn(result);
            result = NULL;
        }
    }

    if (replacement_state && result) {
        if (attach_fd_locked(fileno(result), replacement_state, 0) == 0) {
            replacement_state_attached = 1;
            managed_descriptor_seen = 1;
        }
        else {
            result_errno = ENOMEM;
            real_fclose_fn(result);
            result = NULL;
        }
    }
    if (old_state) pthread_mutex_unlock(&old_state->io_lock);
    pthread_mutex_unlock(&fd_map_lock);
    if (old_state && !preserve_old_state) state_release_reference(old_state);
    if (replacement_state && !replacement_state_attached) {
        state_release_reference(replacement_state);
    }
    if (disposition == 2 && !replacement_lease_consumed) {
        request_release(response.lease_id);
    }

    if (!result) {
        pthread_mutex_unlock(&inheritance_lock);
        errno = result_errno;
        return NULL;
    }

    funlockfile(result);
    pthread_mutex_unlock(&inheritance_lock);
    if (preserve_old_state || disposition == 2) setvbuf(result, NULL, _IONBF, 0);
    return result;
}

FILE *freopen(const char *pathname, const char *mode, FILE *file) {
    return freopen_managed(pathname, mode, file, real_freopen_fn);
}

FILE *freopen64(const char *pathname, const char *mode, FILE *file) {
    return freopen_managed(
        pathname,
        mode,
        file,
        real_freopen64_fn ? real_freopen64_fn : real_freopen_fn);
}

FILE *fdopen(int fd, const char *mode) {
    init_syms();
    shared_state_t *state = acquire_fd_state(fd);
    if (state && !mode_is_read_only(mode)) {
        state_release_reference(state);
        errno = EROFS;
        return NULL;
    }
    FILE *file = real_fdopen_fn(fd, mode);
    if (file && state) setvbuf(file, NULL, _IONBF, 0);
    if (state) state_release_reference(state);
    return file;
}

int close(int fd) {
    init_syms();
    if (reentry) return real_close_fn(fd);
    shared_state_t *state = lock_and_detach_fd_for_close(fd);
    reentry++;
    int result = real_close_fn(fd);
    reentry--;
    unlock_after_close(state);
    return result;
}

static int call_real_close_range(unsigned int first, unsigned int last, int flags) {
    if (real_close_range_fn) return real_close_range_fn(first, last, flags);
#ifdef SYS_close_range
    return (int)syscall(SYS_close_range, first, last, flags);
#else
    errno = ENOSYS;
    return -1;
#endif
}

int close_range(unsigned int first, unsigned int last, int flags) {
    init_syms();
    if ((flags & CLOSE_RANGE_UNSHARE) != 0) {
        // Descriptor state is process-global in the preload shim. Allowing the
        // kernel to split descriptor tables would make a numeric fd refer to
        // different files in sibling threads, so reject the transition before
        // any managed mapping can become ambiguous.
        errno = EOPNOTSUPP;
        return -1;
    }
    if (reentry || (flags & CLOSE_RANGE_CLOEXEC) != 0) {
        return call_real_close_range(first, last, flags);
    }

    pthread_mutex_lock(&fd_map_lock);
    size_t matching_count = 0;
    for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
        unsigned int descriptor = (unsigned int)entry->fd;
        if (entry->fd >= 0 && descriptor >= first && descriptor <= last) matching_count++;
    }

    shared_state_t **unique_states = matching_count
        ? calloc(matching_count, sizeof(*unique_states))
        : NULL;
    shared_state_t **removed_states = matching_count
        ? calloc(matching_count, sizeof(*removed_states))
        : NULL;
    if (matching_count && (!unique_states || !removed_states)) {
        free(unique_states);
        free(removed_states);
        pthread_mutex_unlock(&fd_map_lock);
        errno = ENOMEM;
        return -1;
    }

    size_t unique_count = 0;
    for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
        unsigned int descriptor = (unsigned int)entry->fd;
        if (entry->fd < 0 || descriptor < first || descriptor > last) continue;
        size_t state_index = 0;
        while (state_index < unique_count && unique_states[state_index] != entry->state) {
            state_index++;
        }
        if (state_index == unique_count) unique_states[unique_count++] = entry->state;
    }
    for (size_t state_index = 0; state_index < unique_count; state_index++) {
        pthread_mutex_lock(&unique_states[state_index]->io_lock);
    }

    int result = call_real_close_range(first, last, flags);
    size_t removed_count = 0;
    if (result == 0) {
        fd_entry_t **position = &fd_entries;
        while (*position) {
            unsigned int descriptor = (unsigned int)(*position)->fd;
            if ((*position)->fd >= 0 && descriptor >= first && descriptor <= last) {
                fd_entry_t *removed = *position;
                *position = removed->next;
                removed_states[removed_count++] = removed->state;
                free(removed);
                continue;
            }
            position = &(*position)->next;
        }
    }

    for (size_t state_index = 0; state_index < unique_count; state_index++) {
        pthread_mutex_unlock(&unique_states[state_index]->io_lock);
    }
    pthread_mutex_unlock(&fd_map_lock);
    for (size_t state_index = 0; state_index < removed_count; state_index++) {
        state_release_reference(removed_states[state_index]);
    }
    free(unique_states);
    free(removed_states);
    return result;
}

void closefrom(int low_fd) {
    unsigned int first = low_fd < 0 ? 0u : (unsigned int)low_fd;
    (void)close_range(first, UINT_MAX, 0);
}

static void detach_closed_descriptors(void) {
    fd_entry_t *removed_entries = NULL;
    pthread_mutex_lock(&fd_map_lock);
    fd_entry_t **position = &fd_entries;
    while (*position) {
        errno = 0;
        if (real_fcntl_fn((*position)->fd, F_GETFD) < 0 && errno == EBADF) {
            fd_entry_t *removed = *position;
            *position = removed->next;
            removed->next = removed_entries;
            removed_entries = removed;
            continue;
        }
        position = &(*position)->next;
    }
    pthread_mutex_unlock(&fd_map_lock);

    while (removed_entries) {
        fd_entry_t *next = removed_entries->next;
        state_release_reference(removed_entries->state);
        free(removed_entries);
        removed_entries = next;
    }
}

int fcloseall(void) {
    init_syms();
    if (!real_fcloseall_fn) {
        errno = ENOSYS;
        return EOF;
    }

    reentry++;
    int result = real_fcloseall_fn();
    reentry--;
    int saved_errno = errno;
    detach_closed_descriptors();
    errno = saved_errno;
    return result;
}

int fclose(FILE *file) {
    init_syms();
    int fd = fileno(file);
    shared_state_t *observed = acquire_fd_state(fd);
    if (!observed) return real_fclose_fn(file);
    state_release_reference(observed);
    flockfile(file);
    shared_state_t *state = lock_and_detach_fd_for_close(fd);
    reentry++;
    int result = real_fclose_fn(file);
    reentry--;
    unlock_after_close(state);
    return result;
}

static int force_managed_stream_unbuffered(FILE *file, int *result) {
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return 0;
    reentry++;
    int set_result = real_setvbuf_fn(file, NULL, _IONBF, 0);
    reentry--;
    release_stream_state_after_io(file, state);
    if (result) *result = set_result;
    return 1;
}

int setvbuf(FILE *file, char *buffer, int mode, size_t size) {
    init_syms();
    int result;
    if (force_managed_stream_unbuffered(file, &result)) return result;
    return real_setvbuf_fn(file, buffer, mode, size);
}

void setbuf(FILE *file, char *buffer) {
    init_syms();
    if (force_managed_stream_unbuffered(file, NULL)) return;
    real_setbuf_fn(file, buffer);
}

void setbuffer(FILE *file, char *buffer, size_t size) {
    init_syms();
    if (force_managed_stream_unbuffered(file, NULL)) return;
    if (real_setbuffer_fn) {
        real_setbuffer_fn(file, buffer, size);
    }
    else {
        (void)real_setvbuf_fn(file, buffer, buffer ? _IOFBF : _IONBF, size);
    }
}

void setlinebuf(FILE *file) {
    init_syms();
    if (force_managed_stream_unbuffered(file, NULL)) return;
    if (real_setlinebuf_fn) {
        real_setlinebuf_fn(file);
    }
    else {
        (void)real_setvbuf_fn(file, NULL, _IOLBF, 0);
    }
}

int dup(int old_fd) {
    init_syms();
    pthread_mutex_lock(&fd_map_lock);
    shared_state_t *state = NULL;
    for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
        if (entry->fd == old_fd) {
            state = entry->state;
            break;
        }
    }
    fd_entry_t *new_entry = state ? calloc(1, sizeof(*new_entry)) : NULL;
    if (state && !new_entry) {
        pthread_mutex_unlock(&fd_map_lock);
        errno = ENOMEM;
        return -1;
    }
    if (state) pthread_mutex_lock(&state->io_lock);
    int new_fd = real_dup_fn(old_fd);
    if (new_fd >= 0 && state && force_descriptor_cloexec(new_fd) < 0) {
        int saved_errno = errno;
        real_close_fn(new_fd);
        new_fd = -1;
        errno = saved_errno;
    }
    if (new_fd >= 0 && state) {
        new_entry->fd = new_fd;
        new_entry->state = state;
        state_add_reference(state);
        new_entry->next = fd_entries;
        fd_entries = new_entry;
        new_entry = NULL;
    }
    if (state) pthread_mutex_unlock(&state->io_lock);
    pthread_mutex_unlock(&fd_map_lock);
    free(new_entry);
    return new_fd;
}

static int duplicate_to(int old_fd, int new_fd, int flags, int use_dup3) {
    init_syms();
    if (old_fd == new_fd && use_dup3) {
        errno = EINVAL;
        return -1;
    }
    if (old_fd == new_fd) return real_dup2_fn(old_fd, new_fd);

    pthread_mutex_lock(&fd_map_lock);
    fd_entry_t *source_entry = NULL;
    fd_entry_t **target_position = &fd_entries;
    while (*target_position) {
        if ((*target_position)->fd == old_fd) source_entry = *target_position;
        if ((*target_position)->fd == new_fd) break;
        target_position = &(*target_position)->next;
    }
    if (!source_entry) {
        for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
            if (entry->fd == old_fd) {
                source_entry = entry;
                break;
            }
        }
    }

    fd_entry_t *target_entry = *target_position;
    shared_state_t *source_state = source_entry ? source_entry->state : NULL;
    shared_state_t *old_target_state = target_entry ? target_entry->state : NULL;
    if (source_state && !target_entry && real_fcntl_fn(new_fd, F_GETFD) >= 0) {
        pthread_mutex_unlock(&fd_map_lock);
        errno = EOPNOTSUPP;
        return -1;
    }
    fd_entry_t *replacement_entry = NULL;
    if (source_state) {
        replacement_entry = calloc(1, sizeof(*replacement_entry));
        if (!replacement_entry) {
            pthread_mutex_unlock(&fd_map_lock);
            errno = ENOMEM;
            return -1;
        }
    }

    if (source_state) pthread_mutex_lock(&source_state->io_lock);
    if (old_target_state && old_target_state != source_state) {
        pthread_mutex_lock(&old_target_state->io_lock);
    }
    int result = use_dup3
        ? real_dup3_fn(old_fd, new_fd, flags)
        : real_dup2_fn(old_fd, new_fd);
    if (result < 0) {
        if (old_target_state && old_target_state != source_state) {
            pthread_mutex_unlock(&old_target_state->io_lock);
        }
        if (source_state) pthread_mutex_unlock(&source_state->io_lock);
        pthread_mutex_unlock(&fd_map_lock);
        free(replacement_entry);
        return result;
    }

    int cloexec_error = 0;
    if (source_state && force_descriptor_cloexec(result) < 0) {
        cloexec_error = errno;
        real_close_fn(result);
    }

    if (target_entry) {
        *target_position = target_entry->next;
        free(target_entry);
    }
    if (source_state && !cloexec_error) {
        replacement_entry->fd = result;
        replacement_entry->state = source_state;
        state_add_reference(source_state);
        replacement_entry->next = fd_entries;
        fd_entries = replacement_entry;
    }
    if (old_target_state && old_target_state != source_state) {
        pthread_mutex_unlock(&old_target_state->io_lock);
    }
    if (source_state) pthread_mutex_unlock(&source_state->io_lock);
    pthread_mutex_unlock(&fd_map_lock);
    if (old_target_state) state_release_reference(old_target_state);
    if (cloexec_error) {
        free(replacement_entry);
        errno = cloexec_error;
        return -1;
    }
    return result;
}

int dup2(int old_fd, int new_fd) {
    return duplicate_to(old_fd, new_fd, 0, 0);
}

int dup3(int old_fd, int new_fd, int flags) {
    return duplicate_to(old_fd, new_fd, flags, 1);
}

static int fcntl_with_arguments(
    int fd,
    int command,
    va_list arguments,
    int (*real_function)(int, int, ...)) {
    if (command == F_DUPFD || command == F_DUPFD_CLOEXEC) {
        int minimum_fd = va_arg(arguments, int);
        pthread_mutex_lock(&fd_map_lock);
        shared_state_t *state = NULL;
        for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
            if (entry->fd == fd) {
                state = entry->state;
                break;
            }
        }
        fd_entry_t *new_entry = state ? calloc(1, sizeof(*new_entry)) : NULL;
        if (state && !new_entry) {
            pthread_mutex_unlock(&fd_map_lock);
            errno = ENOMEM;
            return -1;
        }
        if (state) pthread_mutex_lock(&state->io_lock);
        int result = real_function(fd, command, minimum_fd);
        if (result >= 0 && state && force_descriptor_cloexec(result) < 0) {
            int saved_errno = errno;
            real_close_fn(result);
            result = -1;
            errno = saved_errno;
        }
        if (result >= 0 && state) {
            new_entry->fd = result;
            new_entry->state = state;
            state_add_reference(state);
            new_entry->next = fd_entries;
            fd_entries = new_entry;
            new_entry = NULL;
        }
        if (state) pthread_mutex_unlock(&state->io_lock);
        pthread_mutex_unlock(&fd_map_lock);
        free(new_entry);
        return result;
    }

    switch (command) {
        case F_GETFD:
        case F_GETFL:
        case F_GETOWN:
#ifdef F_GETSIG
        case F_GETSIG:
#endif
#ifdef F_GETLEASE
        case F_GETLEASE:
#endif
#ifdef F_GETPIPE_SZ
        case F_GETPIPE_SZ:
#endif
#ifdef F_GET_SEALS
        case F_GET_SEALS:
#endif
            return real_function(fd, command);
        case F_SETFL:
        case F_SETOWN:
#ifdef F_SETSIG
        case F_SETSIG:
#endif
#ifdef F_SETLEASE
        case F_SETLEASE:
#endif
#ifdef F_NOTIFY
        case F_NOTIFY:
#endif
#ifdef F_SETPIPE_SZ
        case F_SETPIPE_SZ:
#endif
        {
            int value = va_arg(arguments, int);
            return real_function(fd, command, value);
        }
        case F_SETFD:
        {
            int value = va_arg(arguments, int);
            pthread_mutex_lock(&fd_map_lock);
            int managed = 0;
            for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
                if (entry->fd == fd) {
                    managed = 1;
                    break;
                }
            }
            int result = real_function(fd, command, managed ? value | FD_CLOEXEC : value);
            pthread_mutex_unlock(&fd_map_lock);
            return result;
        }
        default:
        {
            void *value = va_arg(arguments, void *);
            return real_function(fd, command, value);
        }
    }
}

int fcntl(int fd, int command, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, command);
    int result = fcntl_with_arguments(fd, command, arguments, real_fcntl_fn);
    va_end(arguments);
    return result;
}

int fcntl64(int fd, int command, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, command);
    int result = fcntl_with_arguments(
        fd,
        command,
        arguments,
        real_fcntl64_fn ? real_fcntl64_fn : real_fcntl_fn);
    va_end(arguments);
    return result;
}

static spawn_actions_entry_t **find_spawn_actions_position_locked(
    const posix_spawn_file_actions_t *actions) {
    spawn_actions_entry_t **position = &spawn_actions_entries;
    while (*position && (*position)->actions != actions) {
        position = &(*position)->next;
    }
    return position;
}

static int descriptor_is_managed_locked(int fd) {
    for (fd_entry_t *entry = fd_entries; entry; entry = entry->next) {
        if (entry->fd == fd) return 1;
    }
    return 0;
}

int posix_spawn_file_actions_init(posix_spawn_file_actions_t *actions) {
    init_syms();
    if (!real_posix_spawn_file_actions_init_fn) return ENOSYS;

    spawn_actions_entry_t *entry = calloc(1, sizeof(*entry));
    if (!entry) return ENOMEM;
    int result = real_posix_spawn_file_actions_init_fn(actions);
    if (result != 0) {
        free(entry);
        return result;
    }

    entry->actions = actions;
    pthread_mutex_lock(&spawn_actions_lock);
    entry->next = spawn_actions_entries;
    spawn_actions_entries = entry;
    pthread_mutex_unlock(&spawn_actions_lock);
    return 0;
}

int posix_spawn_file_actions_destroy(posix_spawn_file_actions_t *actions) {
    init_syms();
    if (!real_posix_spawn_file_actions_destroy_fn) return ENOSYS;

    pthread_mutex_lock(&spawn_actions_lock);
    spawn_actions_entry_t **position = find_spawn_actions_position_locked(actions);
    spawn_actions_entry_t *entry = *position;
    int result = real_posix_spawn_file_actions_destroy_fn(actions);
    if (result == 0 && entry) *position = entry->next;
    pthread_mutex_unlock(&spawn_actions_lock);
    if (result != 0 || !entry) return result;

    spawn_dup_action_t *action = entry->dup_actions;
    while (action) {
        spawn_dup_action_t *next = action->next;
        free(action);
        action = next;
    }
    spawn_open_action_t *open_action = entry->open_actions;
    while (open_action) {
        spawn_open_action_t *next = open_action->next;
        free(open_action->path);
        free(open_action);
        open_action = next;
    }
    free(entry);
    return 0;
}

int posix_spawn_file_actions_adddup2(
    posix_spawn_file_actions_t *actions,
    int source_fd,
    int target_fd) {
    init_syms();
    if (!real_posix_spawn_file_actions_adddup2_fn) return ENOSYS;

    spawn_dup_action_t *dup_action = calloc(1, sizeof(*dup_action));
    spawn_actions_entry_t *new_entry = calloc(1, sizeof(*new_entry));
    if (!dup_action || !new_entry) {
        free(dup_action);
        free(new_entry);
        return ENOMEM;
    }

    pthread_mutex_lock(&inheritance_lock);
    pthread_mutex_lock(&spawn_actions_lock);
    pthread_mutex_lock(&fd_map_lock);
    if (descriptor_is_managed_locked(source_fd)) {
        pthread_mutex_unlock(&fd_map_lock);
        pthread_mutex_unlock(&spawn_actions_lock);
        pthread_mutex_unlock(&inheritance_lock);
        free(dup_action);
        free(new_entry);
        return EOPNOTSUPP;
    }

    spawn_actions_entry_t **position = find_spawn_actions_position_locked(actions);
    spawn_actions_entry_t *entry = *position;
    int result = real_posix_spawn_file_actions_adddup2_fn(actions, source_fd, target_fd);
    if (result == 0) {
        if (!entry) {
            new_entry->actions = actions;
            new_entry->next = spawn_actions_entries;
            spawn_actions_entries = new_entry;
            entry = new_entry;
            new_entry = NULL;
        }
        dup_action->source_fd = source_fd;
        dup_action->next = entry->dup_actions;
        entry->dup_actions = dup_action;
        dup_action = NULL;
    }
    pthread_mutex_unlock(&fd_map_lock);
    pthread_mutex_unlock(&spawn_actions_lock);
    pthread_mutex_unlock(&inheritance_lock);
    free(dup_action);
    free(new_entry);
    return result;
}

int posix_spawn_file_actions_addchdir_np(
    posix_spawn_file_actions_t *actions,
    const char *path) {
    init_syms();
    if (!real_posix_spawn_file_actions_addchdir_np_fn) return ENOSYS;

    spawn_actions_entry_t *new_entry = calloc(1, sizeof(*new_entry));
    if (!new_entry) return ENOMEM;

    pthread_mutex_lock(&inheritance_lock);
    pthread_mutex_lock(&spawn_actions_lock);
    spawn_actions_entry_t **position = find_spawn_actions_position_locked(actions);
    spawn_actions_entry_t *entry = *position;
    int result = real_posix_spawn_file_actions_addchdir_np_fn(actions, path);
    if (result == 0) {
        if (!entry) {
            new_entry->actions = actions;
            new_entry->next = spawn_actions_entries;
            spawn_actions_entries = new_entry;
            entry = new_entry;
            new_entry = NULL;
        }
        entry->directory_changed = 1;
    }
    pthread_mutex_unlock(&spawn_actions_lock);
    pthread_mutex_unlock(&inheritance_lock);
    free(new_entry);
    return result;
}

int posix_spawn_file_actions_addfchdir_np(
    posix_spawn_file_actions_t *actions,
    int directory_fd) {
    init_syms();
    if (!real_posix_spawn_file_actions_addfchdir_np_fn) return ENOSYS;

    spawn_actions_entry_t *new_entry = calloc(1, sizeof(*new_entry));
    if (!new_entry) return ENOMEM;

    pthread_mutex_lock(&inheritance_lock);
    pthread_mutex_lock(&spawn_actions_lock);
    spawn_actions_entry_t **position = find_spawn_actions_position_locked(actions);
    spawn_actions_entry_t *entry = *position;
    int result = real_posix_spawn_file_actions_addfchdir_np_fn(actions, directory_fd);
    if (result == 0) {
        if (!entry) {
            new_entry->actions = actions;
            new_entry->next = spawn_actions_entries;
            spawn_actions_entries = new_entry;
            entry = new_entry;
            new_entry = NULL;
        }
        entry->directory_changed = 1;
    }
    pthread_mutex_unlock(&spawn_actions_lock);
    pthread_mutex_unlock(&inheritance_lock);
    free(new_entry);
    return result;
}

int posix_spawn_file_actions_addopen(
    posix_spawn_file_actions_t *actions,
    int target_fd,
    const char *path,
    int flags,
    mode_t mode) {
    init_syms();
    if (!real_posix_spawn_file_actions_addopen_fn) return ENOSYS;
    if (path_requires_vfs_handling(path)) return EOPNOTSUPP;

    spawn_open_action_t *open_action = calloc(1, sizeof(*open_action));
    spawn_actions_entry_t *new_entry = calloc(1, sizeof(*new_entry));
    char *path_copy = strdup(path);
    if (!open_action || !new_entry || !path_copy) {
        free(open_action);
        free(new_entry);
        free(path_copy);
        return ENOMEM;
    }

    pthread_mutex_lock(&inheritance_lock);
    pthread_mutex_lock(&spawn_actions_lock);
    spawn_actions_entry_t **position = find_spawn_actions_position_locked(actions);
    spawn_actions_entry_t *entry = *position;
    if (entry && entry->directory_changed && path[0] != '/') {
        pthread_mutex_unlock(&spawn_actions_lock);
        pthread_mutex_unlock(&inheritance_lock);
        free(open_action);
        free(new_entry);
        free(path_copy);
        return EOPNOTSUPP;
    }
    int result = real_posix_spawn_file_actions_addopen_fn(
        actions,
        target_fd,
        path,
        flags,
        mode);
    if (result == 0) {
        if (!entry) {
            new_entry->actions = actions;
            new_entry->next = spawn_actions_entries;
            spawn_actions_entries = new_entry;
            entry = new_entry;
            new_entry = NULL;
        }
        open_action->path = path_copy;
        path_copy = NULL;
        open_action->next = entry->open_actions;
        entry->open_actions = open_action;
        open_action = NULL;
    }
    pthread_mutex_unlock(&spawn_actions_lock);
    pthread_mutex_unlock(&inheritance_lock);
    free(open_action);
    free(new_entry);
    free(path_copy);
    return result;
}

static int spawn_actions_export_managed_descriptor_locked(
    const posix_spawn_file_actions_t *actions) {
    if (!actions) return 0;
    spawn_actions_entry_t *entry = *find_spawn_actions_position_locked(actions);
    if (!entry) return managed_descriptor_seen;
    for (spawn_dup_action_t *action = entry->dup_actions; action; action = action->next) {
        if (descriptor_is_managed_locked(action->source_fd)) return 1;
    }
    for (spawn_open_action_t *action = entry->open_actions; action; action = action->next) {
        if (path_requires_vfs_handling(action->path)) return 1;
    }
    return 0;
}

static int spawn_process(
    int (*real_function)(
        pid_t *,
        const char *,
        const posix_spawn_file_actions_t *,
        const posix_spawnattr_t *,
        char *const[],
        char *const[]),
    pid_t *process_id,
    const char *path,
    const posix_spawn_file_actions_t *actions,
    const posix_spawnattr_t *attributes,
    char *const arguments[],
    char *const environment[]) {
    if (!real_function) return ENOSYS;

    pthread_mutex_lock(&inheritance_lock);
    pthread_mutex_lock(&spawn_actions_lock);
    pthread_mutex_lock(&fd_map_lock);
    if (spawn_actions_export_managed_descriptor_locked(actions)) {
        pthread_mutex_unlock(&fd_map_lock);
        pthread_mutex_unlock(&spawn_actions_lock);
        pthread_mutex_unlock(&inheritance_lock);
        return EOPNOTSUPP;
    }

    int result = real_function(
        process_id,
        path,
        actions,
        attributes,
        arguments,
        environment);
    pthread_mutex_unlock(&fd_map_lock);
    pthread_mutex_unlock(&spawn_actions_lock);
    pthread_mutex_unlock(&inheritance_lock);
    return result;
}

int posix_spawn(
    pid_t *process_id,
    const char *path,
    const posix_spawn_file_actions_t *actions,
    const posix_spawnattr_t *attributes,
    char *const arguments[],
    char *const environment[]) {
    init_syms();
    return spawn_process(
        real_posix_spawn_fn,
        process_id,
        path,
        actions,
        attributes,
        arguments,
        environment);
}

int posix_spawnp(
    pid_t *process_id,
    const char *file,
    const posix_spawn_file_actions_t *actions,
    const posix_spawnattr_t *attributes,
    char *const arguments[],
    char *const environment[]) {
    init_syms();
    return spawn_process(
        real_posix_spawnp_fn,
        process_id,
        file,
        actions,
        attributes,
        arguments,
        environment);
}

pid_t fork(void) {
    init_syms();
    if (!real_fork_fn) {
        errno = ENOSYS;
        return -1;
    }

    pthread_mutex_lock(&inheritance_lock);
    if (managed_descriptor_seen) {
        pthread_mutex_unlock(&inheritance_lock);
        errno = EOPNOTSUPP;
        return -1;
    }

    pid_t result = real_fork_fn();
    pthread_mutex_unlock(&inheritance_lock);
    return result;
}

pid_t vfork(void) {
    init_syms();
    if (!real_vfork_fn) {
        errno = ENOSYS;
        return -1;
    }

    pthread_mutex_lock(&inheritance_lock);
    if (managed_descriptor_seen) {
        pthread_mutex_unlock(&inheritance_lock);
        errno = EOPNOTSUPP;
        return -1;
    }

    pid_t result = real_vfork_fn();
    if (result != 0) pthread_mutex_unlock(&inheritance_lock);
    return result;
}

int clone(int (*function)(void *), void *child_stack, int flags, void *argument, ...) {
    init_syms();
    if (!real_clone_fn) {
        errno = ENOSYS;
        return -1;
    }

    pthread_mutex_lock(&inheritance_lock);
    const int shared_process_flags = CLONE_VM | CLONE_FILES | CLONE_THREAD;
    if (managed_descriptor_seen &&
        (flags & shared_process_flags) != shared_process_flags) {
        pthread_mutex_unlock(&inheritance_lock);
        errno = EOPNOTSUPP;
        return -1;
    }

    void *parent_tid = NULL;
    void *tls = NULL;
    void *child_tid = NULL;
    va_list arguments;
    va_start(arguments, argument);
    if ((flags & (CLONE_CHILD_SETTID | CLONE_CHILD_CLEARTID)) != 0) {
        parent_tid = va_arg(arguments, void *);
        tls = va_arg(arguments, void *);
        child_tid = va_arg(arguments, void *);
    }
    else if ((flags & CLONE_SETTLS) != 0) {
        parent_tid = va_arg(arguments, void *);
        tls = va_arg(arguments, void *);
    }
    else if ((flags & (CLONE_PARENT_SETTID | CLONE_PIDFD)) != 0) {
        parent_tid = va_arg(arguments, void *);
    }
    va_end(arguments);

    int result = real_clone_fn(
        function,
        child_stack,
        flags,
        argument,
        parent_tid,
        tls,
        child_tid);
    pthread_mutex_unlock(&inheritance_lock);
    return result;
}

static int message_exports_managed_descriptor_locked(const struct msghdr *message) {
    if (!message || !message->msg_control || message->msg_controllen < sizeof(struct cmsghdr)) {
        return 0;
    }

    uintptr_t control_start = (uintptr_t)message->msg_control;
    if (message->msg_controllen > UINTPTR_MAX - control_start) return 0;
    uintptr_t control_end = control_start + message->msg_controllen;

    struct msghdr *mutable_message = (struct msghdr *)message;
    for (struct cmsghdr *header = CMSG_FIRSTHDR(mutable_message);
         header;
         header = CMSG_NXTHDR(mutable_message, header)) {
        uintptr_t header_start = (uintptr_t)header;
        if (header_start < control_start ||
            header_start > control_end ||
            control_end - header_start < sizeof(*header) ||
            header->cmsg_len > control_end - header_start) break;
        if (header->cmsg_len < CMSG_LEN(0)) break;
        if (header->cmsg_level != SOL_SOCKET || header->cmsg_type != SCM_RIGHTS) continue;

        size_t descriptor_bytes = header->cmsg_len - CMSG_LEN(0);
        size_t descriptor_count = descriptor_bytes / sizeof(int);
        const unsigned char *descriptor_data = CMSG_DATA(header);
        for (size_t index = 0; index < descriptor_count; index++) {
            int descriptor;
            memcpy(
                &descriptor,
                descriptor_data + (index * sizeof(descriptor)),
                sizeof(descriptor));
            if (descriptor_is_managed_locked(descriptor)) return 1;
        }
    }

    return 0;
}

ssize_t sendmsg(int socket_fd, const struct msghdr *message, int flags) {
    init_syms();
    if (!real_sendmsg_fn) {
        errno = ENOSYS;
        return -1;
    }

    pthread_mutex_lock(&inheritance_lock);
    pthread_mutex_lock(&fd_map_lock);
    int exports_managed = message_exports_managed_descriptor_locked(message);
    pthread_mutex_unlock(&fd_map_lock);
    if (exports_managed) {
        pthread_mutex_unlock(&inheritance_lock);
        errno = EOPNOTSUPP;
        return -1;
    }

    ssize_t result = real_sendmsg_fn(socket_fd, message, flags);
    pthread_mutex_unlock(&inheritance_lock);
    return result;
}

int sendmmsg(
    int socket_fd,
    struct mmsghdr *messages,
    unsigned int message_count,
    int flags) {
    init_syms();
    if (!real_sendmmsg_fn) {
        errno = ENOSYS;
        return -1;
    }

    pthread_mutex_lock(&inheritance_lock);
    pthread_mutex_lock(&fd_map_lock);
    int exports_managed = 0;
    for (unsigned int index = 0; index < message_count; index++) {
        if (message_exports_managed_descriptor_locked(&messages[index].msg_hdr)) {
            exports_managed = 1;
            break;
        }
    }
    pthread_mutex_unlock(&fd_map_lock);
    if (exports_managed) {
        pthread_mutex_unlock(&inheritance_lock);
        errno = EOPNOTSUPP;
        return -1;
    }

    int result = real_sendmmsg_fn(socket_fd, messages, message_count, flags);
    pthread_mutex_unlock(&inheritance_lock);
    return result;
}

ssize_t read(int fd, void *buffer, size_t count) {
    init_syms();
    if (reentry) return real_read_fn(fd, buffer, count);
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_read_fn(fd, buffer, count);
    off_t offset = real_lseek_fn(fd, 0, SEEK_CUR);
    ssize_t result = -1;
    if (offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0) {
        result = real_read_fn(fd, buffer, count);
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t __read_chk(int fd, void *buffer, size_t count, size_t buffer_length) {
    init_syms();
    if (!real___read_chk_fn) {
        errno = ENOSYS;
        return -1;
    }
    if (reentry) return real___read_chk_fn(fd, buffer, count, buffer_length);
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real___read_chk_fn(fd, buffer, count, buffer_length);
    off_t offset = real_lseek_fn(fd, 0, SEEK_CUR);
    ssize_t result = -1;
    if (offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0) {
        result = real___read_chk_fn(fd, buffer, count, buffer_length);
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t pread(int fd, void *buffer, size_t count, off_t offset) {
    init_syms();
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_pread_fn(fd, buffer, count, offset);
    ssize_t result = offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0
        ? real_pread_fn(fd, buffer, count, offset)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t pread64(int fd, void *buffer, size_t count, off64_t offset) {
    init_syms();
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_pread64_fn(fd, buffer, count, offset);
    ssize_t result = offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0
        ? real_pread64_fn(fd, buffer, count, offset)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t __pread_chk(int fd, void *buffer, size_t count, off_t offset, size_t buffer_length) {
    init_syms();
    if (!real___pread_chk_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real___pread_chk_fn(fd, buffer, count, offset, buffer_length);
    ssize_t result = offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0
        ? real___pread_chk_fn(fd, buffer, count, offset, buffer_length)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t __pread64_chk(int fd, void *buffer, size_t count, off64_t offset, size_t buffer_length) {
    init_syms();
    if (!real___pread64_chk_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real___pread64_chk_fn(fd, buffer, count, offset, buffer_length);
    ssize_t result = offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0
        ? real___pread64_chk_fn(fd, buffer, count, offset, buffer_length)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

static int ensure_aio_range(int fd, off64_t offset, size_t count) {
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return 0;
    int result = -1;
    if (offset < 0) {
        errno = EINVAL;
    }
    else if (ensure_range_locked(state, (uint64_t)offset, count) == 0) {
        result = 0;
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

int aio_read(struct aiocb *control_block) {
    init_syms();
    if (!real_aio_read_fn) {
        errno = ENOSYS;
        return -1;
    }
    if (ensure_aio_range(
            control_block->aio_fildes,
            (off64_t)control_block->aio_offset,
            control_block->aio_nbytes) != 0) {
        return -1;
    }
    return real_aio_read_fn(control_block);
}

int aio_read64(struct aiocb64 *control_block) {
    init_syms();
    if (!real_aio_read64_fn) {
        errno = ENOSYS;
        return -1;
    }
    if (ensure_aio_range(
            control_block->aio_fildes,
            control_block->aio_offset,
            control_block->aio_nbytes) != 0) {
        return -1;
    }
    return real_aio_read64_fn(control_block);
}

int lio_listio(
    int mode,
    struct aiocb *const list[],
    int entry_count,
    struct sigevent *signal_event) {
    init_syms();
    if (!real_lio_listio_fn) {
        errno = ENOSYS;
        return -1;
    }
    for (int index = 0; index < entry_count; index++) {
        struct aiocb *control_block = list[index];
        if (!control_block || control_block->aio_lio_opcode != LIO_READ) continue;
        if (ensure_aio_range(
                control_block->aio_fildes,
                (off64_t)control_block->aio_offset,
                control_block->aio_nbytes) != 0) {
            return -1;
        }
    }
    return real_lio_listio_fn(mode, list, entry_count, signal_event);
}

int lio_listio64(
    int mode,
    struct aiocb64 *const list[],
    int entry_count,
    struct sigevent *signal_event) {
    init_syms();
    if (!real_lio_listio64_fn) {
        errno = ENOSYS;
        return -1;
    }
    for (int index = 0; index < entry_count; index++) {
        struct aiocb64 *control_block = list[index];
        if (!control_block || control_block->aio_lio_opcode != LIO_READ) continue;
        if (ensure_aio_range(
                control_block->aio_fildes,
                control_block->aio_offset,
                control_block->aio_nbytes) != 0) {
            return -1;
        }
    }
    return real_lio_listio64_fn(mode, list, entry_count, signal_event);
}

ssize_t sendfile(int output_fd, int input_fd, off_t *offset, size_t count) {
    init_syms();
    if (!real_sendfile_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(input_fd);
    if (!state) return real_sendfile_fn(output_fd, input_fd, offset, count);
    off_t effective_offset = offset ? *offset : real_lseek_fn(input_fd, 0, SEEK_CUR);
    ssize_t result = -1;
    if (effective_offset >= 0 &&
        ensure_range_locked(state, (uint64_t)effective_offset, count) == 0) {
        reentry++;
        result = real_sendfile_fn(output_fd, input_fd, offset, count);
        reentry--;
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t sendfile64(int output_fd, int input_fd, off64_t *offset, size_t count) {
    init_syms();
    if (!real_sendfile64_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(input_fd);
    if (!state) return real_sendfile64_fn(output_fd, input_fd, offset, count);
    off64_t effective_offset = offset ? *offset : real_lseek64_fn(input_fd, 0, SEEK_CUR);
    ssize_t result = -1;
    if (effective_offset >= 0 &&
        ensure_range_locked(state, (uint64_t)effective_offset, count) == 0) {
        reentry++;
        result = real_sendfile64_fn(output_fd, input_fd, offset, count);
        reentry--;
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t copy_file_range(
    int input_fd,
    off64_t *input_offset,
    int output_fd,
    off64_t *output_offset,
    size_t length,
    unsigned int flags) {
    init_syms();
    if (!real_copy_file_range_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(input_fd);
    if (!state) {
        return real_copy_file_range_fn(
            input_fd,
            input_offset,
            output_fd,
            output_offset,
            length,
            flags);
    }
    off64_t effective_offset = input_offset
        ? *input_offset
        : real_lseek64_fn(input_fd, 0, SEEK_CUR);
    ssize_t result = -1;
    if (effective_offset >= 0 &&
        ensure_range_locked(state, (uint64_t)effective_offset, length) == 0) {
        reentry++;
        result = real_copy_file_range_fn(
            input_fd,
            input_offset,
            output_fd,
            output_offset,
            length,
            flags);
        reentry--;
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t splice(
    int input_fd,
    off64_t *input_offset,
    int output_fd,
    off64_t *output_offset,
    size_t length,
    unsigned int flags) {
    init_syms();
    if (!real_splice_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(input_fd);
    if (!state) {
        return real_splice_fn(
            input_fd,
            input_offset,
            output_fd,
            output_offset,
            length,
            flags);
    }
    off64_t effective_offset = input_offset
        ? *input_offset
        : real_lseek64_fn(input_fd, 0, SEEK_CUR);
    ssize_t result = -1;
    if (effective_offset >= 0 &&
        ensure_range_locked(state, (uint64_t)effective_offset, length) == 0) {
        reentry++;
        result = real_splice_fn(
            input_fd,
            input_offset,
            output_fd,
            output_offset,
            length,
            flags);
        reentry--;
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

int ioctl(int fd, unsigned long request, ...) {
    init_syms();
    if (!real_ioctl_fn) {
        errno = ENOSYS;
        return -1;
    }

    if (request == FIONCLEX || request == FIOCLEX) {
        if (request == FIONCLEX) {
            pthread_mutex_lock(&fd_map_lock);
            int managed = descriptor_is_managed_locked(fd);
            pthread_mutex_unlock(&fd_map_lock);
            if (managed) {
                errno = EOPNOTSUPP;
                return -1;
            }
        }
        return real_ioctl_fn(fd, request);
    }

    va_list arguments;
    va_start(arguments, request);
    if (request == FICLONE) {
        int source_fd = va_arg(arguments, int);
        va_end(arguments);
        shared_state_t *state = acquire_fd_state_for_io(source_fd);
        if (!state) return real_ioctl_fn(fd, request, source_fd);
        int result = -1;
        if (ensure_complete_locked(state, "reflink_clone") == 0) {
            reentry++;
            result = real_ioctl_fn(fd, request, source_fd);
            reentry--;
        }
        pthread_mutex_unlock(&state->io_lock);
        state_release_reference(state);
        return result;
    }

    void *argument = va_arg(arguments, void *);
    va_end(arguments);
    if (request == FS_IOC_FIEMAP) {
        shared_state_t *state = acquire_fd_state_for_io(fd);
        if (!state) return real_ioctl_fn(fd, request, argument);
        int result = -1;
        if (ensure_complete_locked(state, "fiemap") == 0) {
            reentry++;
            result = real_ioctl_fn(fd, request, argument);
            reentry--;
        }
        pthread_mutex_unlock(&state->io_lock);
        state_release_reference(state);
        return result;
    }
    if (request != FICLONERANGE) return real_ioctl_fn(fd, request, argument);

    struct file_clone_range clone_range;
    struct iovec local = {
        .iov_base = &clone_range,
        .iov_len = sizeof(clone_range)
    };
    struct iovec remote = {
        .iov_base = argument,
        .iov_len = sizeof(clone_range)
    };
    if (!argument ||
        process_vm_readv(getpid(), &local, 1, &remote, 1, 0) !=
            (ssize_t)sizeof(clone_range)) {
        errno = EFAULT;
        return -1;
    }

    shared_state_t *state = acquire_fd_state_for_io((int)clone_range.src_fd);
    if (!state) return real_ioctl_fn(fd, request, argument);
    uint64_t source_length = clone_range.src_length;
    if (source_length == 0 && clone_range.src_offset < (uint64_t)state->expected_length) {
        source_length = (uint64_t)state->expected_length - clone_range.src_offset;
    }
    int result = -1;
    if (ensure_range_locked(state, clone_range.src_offset, source_length) == 0) {
        reentry++;
        result = real_ioctl_fn(fd, request, argument);
        reentry--;
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t readv(int fd, const struct iovec *iov, int iov_count) {
    init_syms();
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_readv_fn(fd, iov, iov_count);
    off_t offset = real_lseek_fn(fd, 0, SEEK_CUR);
    size_t count = iov_length(iov, iov_count);
    ssize_t result = offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0
        ? real_readv_fn(fd, iov, iov_count)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t preadv(int fd, const struct iovec *iov, int iov_count, off_t offset) {
    init_syms();
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_preadv_fn(fd, iov, iov_count, offset);
    size_t count = iov_length(iov, iov_count);
    ssize_t result = offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0
        ? real_preadv_fn(fd, iov, iov_count, offset)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t preadv2(int fd, const struct iovec *iov, int iov_count, off_t offset, int flags) {
    init_syms();
    if (!real_preadv2_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_preadv2_fn(fd, iov, iov_count, offset, flags);
    size_t count = iov_length(iov, iov_count);
    off_t effective_offset = offset;
    if (offset == -1) {
        effective_offset = real_lseek_fn(fd, 0, SEEK_CUR);
    }
    ssize_t result = effective_offset >= 0 &&
                     ensure_range_locked(state, (uint64_t)effective_offset, count) == 0
        ? real_preadv2_fn(fd, iov, iov_count, offset, flags)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t preadv64(int fd, const struct iovec *iov, int iov_count, off64_t offset) {
    init_syms();
    if (!real_preadv64_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_preadv64_fn(fd, iov, iov_count, offset);
    size_t count = iov_length(iov, iov_count);
    ssize_t result = offset >= 0 && ensure_range_locked(state, (uint64_t)offset, count) == 0
        ? real_preadv64_fn(fd, iov, iov_count, offset)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

ssize_t preadv64v2(int fd, const struct iovec *iov, int iov_count, off64_t offset, int flags) {
    init_syms();
    if (!real_preadv64v2_fn) {
        errno = ENOSYS;
        return -1;
    }
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_preadv64v2_fn(fd, iov, iov_count, offset, flags);
    size_t count = iov_length(iov, iov_count);
    off64_t effective_offset = offset;
    if (offset == -1) {
        effective_offset = real_lseek64_fn(fd, 0, SEEK_CUR);
    }
    ssize_t result = effective_offset >= 0 &&
                     ensure_range_locked(state, (uint64_t)effective_offset, count) == 0
        ? real_preadv64v2_fn(fd, iov, iov_count, offset, flags)
        : -1;
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

off_t lseek(int fd, off_t offset, int whence) {
    init_syms();
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_lseek_fn(fd, offset, whence);
    off_t result = -1;
    if ((whence != SEEK_DATA && whence != SEEK_HOLE) ||
        ensure_complete_locked(state, "extent_seek") == 0) {
        result = real_lseek_fn(fd, offset, whence);
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

off64_t lseek64(int fd, off64_t offset, int whence) {
    init_syms();
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_lseek64_fn(fd, offset, whence);
    off64_t result = -1;
    if ((whence != SEEK_DATA && whence != SEEK_HOLE) ||
        ensure_complete_locked(state, "extent_seek") == 0) {
        result = real_lseek64_fn(fd, offset, whence);
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

static void mark_stdio_error(FILE *file) {
#if defined(__GLIBC__) && defined(_IO_ERR_SEEN)
    // There is no standard setter for a FILE error indicator. Runner images
    // use glibc, whose public FILE definition exposes the same flag queried
    // by ferror and cleared by clearerr.
    file->_flags |= _IO_ERR_SEEN;
#else
#error "model_vfs requires libc support for setting the FILE error indicator"
#endif
}

static size_t fread_managed(
    void *buffer,
    size_t size,
    size_t count,
    FILE *file,
    int unlocked) {
    init_syms();
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) {
        return unlocked
            ? real_fread_unlocked_fn(buffer, size, count, file)
            : real_fread_fn(buffer, size, count, file);
    }
    int fd = fileno(file);
    off_t offset = real_lseek_fn(fd, 0, SEEK_CUR);
    size_t length = size != 0 && count > SIZE_MAX / size ? SIZE_MAX : size * count;
    size_t result = 0;
    if (offset >= 0 && ensure_range_locked(state, (uint64_t)offset, length) == 0) {
        reentry++;
        result = unlocked
            ? real_fread_unlocked_fn(buffer, size, count, file)
            : real_fread_fn(buffer, size, count, file);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

size_t fread(void *buffer, size_t size, size_t count, FILE *file) {
    return fread_managed(buffer, size, count, file, 0);
}

size_t fread_unlocked(void *buffer, size_t size, size_t count, FILE *file) {
    return fread_managed(buffer, size, count, file, 1);
}

static size_t fread_chk_managed(
    void *buffer,
    size_t buffer_length,
    size_t size,
    size_t count,
    FILE *file,
    int unlocked) {
    init_syms();
    size_t (*real_function)(void *, size_t, size_t, size_t, FILE *) = unlocked
        ? real___fread_unlocked_chk_fn
        : real___fread_chk_fn;
    if (!real_function) {
        errno = ENOSYS;
        return 0;
    }
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(buffer, buffer_length, size, count, file);
    int fd = fileno(file);
    off_t offset = real_lseek_fn(fd, 0, SEEK_CUR);
    size_t length = size != 0 && count > SIZE_MAX / size ? SIZE_MAX : size * count;
    size_t result = 0;
    if (offset >= 0 && ensure_range_locked(state, (uint64_t)offset, length) == 0) {
        reentry++;
        result = real_function(buffer, buffer_length, size, count, file);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

size_t __fread_chk(void *buffer, size_t buffer_length, size_t size, size_t count, FILE *file) {
    return fread_chk_managed(buffer, buffer_length, size, count, file, 0);
}

size_t __fread_unlocked_chk(void *buffer, size_t buffer_length, size_t size, size_t count, FILE *file) {
    return fread_chk_managed(buffer, buffer_length, size, count, file, 1);
}

static int fgetc_managed(FILE *file, int (*real_function)(FILE *)) {
    init_syms();
    if (!real_function) real_function = real_fgetc_fn;
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(file);
    int fd = fileno(file);
    off_t offset = real_lseek_fn(fd, 0, SEEK_CUR);
    int result = EOF;
    if (offset >= 0 && ensure_range_locked(state, (uint64_t)offset, 1) == 0) {
        reentry++;
        result = real_function(file);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

#undef getc
#undef getchar
#undef getc_unlocked
#undef getchar_unlocked

int fgetc(FILE *file) {
    return fgetc_managed(file, real_fgetc_fn);
}

int getc(FILE *file) {
    return fgetc_managed(file, real_getc_fn);
}

int getchar(void) {
    return fgetc(stdin);
}

int fgetc_unlocked(FILE *file) {
    return fgetc_managed(file, real_fgetc_unlocked_fn);
}

int getc_unlocked(FILE *file) {
    return fgetc_managed(file, real_getc_unlocked_fn);
}

int __uflow(FILE *file) {
    init_syms();
    if (!real___uflow_fn) {
        errno = ENOSYS;
        return EOF;
    }
    if (reentry) return real___uflow_fn(file);
    return fgetc_managed(file, real___uflow_fn);
}

int getchar_unlocked(void) {
    return fgetc_unlocked(stdin);
}

int getw(FILE *file) {
    int value = 0;
    return fread_managed(&value, sizeof(value), 1, file, 0) == 1 ? value : EOF;
}

static char *fgets_managed(
    char *buffer,
    int size,
    FILE *file,
    char *(*real_function)(char *, int, FILE *)) {
    init_syms();
    if (!real_function) real_function = real_fgets_fn;
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(buffer, size, file);
    int fd = fileno(file);
    off_t offset = real_lseek_fn(fd, 0, SEEK_CUR);
    size_t length = size > 1 ? (size_t)(size - 1) : 0;
    char *result = NULL;
    if (offset >= 0 && (length == 0 || ensure_range_locked(state, (uint64_t)offset, length) == 0)) {
        reentry++;
        result = real_function(buffer, size, file);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

char *fgets(char *buffer, int size, FILE *file) {
    return fgets_managed(buffer, size, file, real_fgets_fn);
}

char *fgets_unlocked(char *buffer, int size, FILE *file) {
    return fgets_managed(buffer, size, file, real_fgets_unlocked_fn);
}

static ssize_t getdelim_managed(
    char **line,
    size_t *capacity,
    int delimiter,
    FILE *file,
    ssize_t (*real_function)(char **, size_t *, int, FILE *)) {
    init_syms();
    if (!real_function) real_function = real_getdelim_fn;
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(line, capacity, delimiter, file);
    ssize_t result = -1;
    if (ensure_complete_locked(state, "line_read") == 0) {
        reentry++;
        result = real_function(line, capacity, delimiter, file);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

ssize_t getdelim(char **line, size_t *capacity, int delimiter, FILE *file) {
    return getdelim_managed(line, capacity, delimiter, file, real_getdelim_fn);
}

ssize_t __getdelim(char **line, size_t *capacity, int delimiter, FILE *file) {
    return getdelim_managed(line, capacity, delimiter, file, real___getdelim_fn);
}

ssize_t getline(char **line, size_t *capacity, FILE *file) {
    return getdelim_managed(line, capacity, '\n', file, real_getdelim_fn);
}

static int vfscanf_managed(
    FILE *file,
    const char *format,
    va_list arguments,
    int (*real_function)(FILE *, const char *, va_list)) {
    init_syms();
    if (!real_function) {
        errno = ENOSYS;
        return EOF;
    }
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(file, format, arguments);
    int result = EOF;
    if (ensure_complete_locked(state, "formatted_read") == 0) {
        reentry++;
        result = real_function(file, format, arguments);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

int model_vfscanf(FILE *file, const char *format, va_list arguments) __asm__("vfscanf");
int model_vfscanf(FILE *file, const char *format, va_list arguments) {
    init_syms();
    return vfscanf_managed(file, format, arguments, real_vfscanf_fn);
}

int model_fscanf(FILE *file, const char *format, ...) __asm__("fscanf");
int model_fscanf(FILE *file, const char *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfscanf_managed(file, format, arguments, real_vfscanf_fn);
    va_end(arguments);
    return result;
}

int model_isoc99_vfscanf(FILE *file, const char *format, va_list arguments) __asm__("__isoc99_vfscanf");
int model_isoc99_vfscanf(FILE *file, const char *format, va_list arguments) {
    init_syms();
    return vfscanf_managed(
        file,
        format,
        arguments,
        real___isoc99_vfscanf_fn ? real___isoc99_vfscanf_fn : real_vfscanf_fn);
}

int model_isoc99_fscanf(FILE *file, const char *format, ...) __asm__("__isoc99_fscanf");
int model_isoc99_fscanf(FILE *file, const char *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfscanf_managed(
        file,
        format,
        arguments,
        real___isoc99_vfscanf_fn ? real___isoc99_vfscanf_fn : real_vfscanf_fn);
    va_end(arguments);
    return result;
}

int model_isoc23_vfscanf(FILE *file, const char *format, va_list arguments) __asm__("__isoc23_vfscanf");
int model_isoc23_vfscanf(FILE *file, const char *format, va_list arguments) {
    init_syms();
    return vfscanf_managed(
        file,
        format,
        arguments,
        real___isoc23_vfscanf_fn
            ? real___isoc23_vfscanf_fn
            : (real___isoc99_vfscanf_fn ? real___isoc99_vfscanf_fn : real_vfscanf_fn));
}

int model_isoc23_fscanf(FILE *file, const char *format, ...) __asm__("__isoc23_fscanf");
int model_isoc23_fscanf(FILE *file, const char *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfscanf_managed(
        file,
        format,
        arguments,
        real___isoc23_vfscanf_fn
            ? real___isoc23_vfscanf_fn
            : (real___isoc99_vfscanf_fn ? real___isoc99_vfscanf_fn : real_vfscanf_fn));
    va_end(arguments);
    return result;
}

int model_vscanf(const char *format, va_list arguments) __asm__("vscanf");
int model_vscanf(const char *format, va_list arguments) {
    init_syms();
    return vfscanf_managed(stdin, format, arguments, real_vfscanf_fn);
}

int model_scanf(const char *format, ...) __asm__("scanf");
int model_scanf(const char *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfscanf_managed(stdin, format, arguments, real_vfscanf_fn);
    va_end(arguments);
    return result;
}

int model_isoc99_vscanf(const char *format, va_list arguments) __asm__("__isoc99_vscanf");
int model_isoc99_vscanf(const char *format, va_list arguments) {
    init_syms();
    return vfscanf_managed(
        stdin,
        format,
        arguments,
        real___isoc99_vfscanf_fn ? real___isoc99_vfscanf_fn : real_vfscanf_fn);
}

int model_isoc99_scanf(const char *format, ...) __asm__("__isoc99_scanf");
int model_isoc99_scanf(const char *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfscanf_managed(
        stdin,
        format,
        arguments,
        real___isoc99_vfscanf_fn ? real___isoc99_vfscanf_fn : real_vfscanf_fn);
    va_end(arguments);
    return result;
}

int model_isoc23_vscanf(const char *format, va_list arguments) __asm__("__isoc23_vscanf");
int model_isoc23_vscanf(const char *format, va_list arguments) {
    init_syms();
    return vfscanf_managed(
        stdin,
        format,
        arguments,
        real___isoc23_vfscanf_fn
            ? real___isoc23_vfscanf_fn
            : (real___isoc99_vfscanf_fn ? real___isoc99_vfscanf_fn : real_vfscanf_fn));
}

int model_isoc23_scanf(const char *format, ...) __asm__("__isoc23_scanf");
int model_isoc23_scanf(const char *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfscanf_managed(
        stdin,
        format,
        arguments,
        real___isoc23_vfscanf_fn
            ? real___isoc23_vfscanf_fn
            : (real___isoc99_vfscanf_fn ? real___isoc99_vfscanf_fn : real_vfscanf_fn));
    va_end(arguments);
    return result;
}

static wint_t fgetwc_managed(FILE *file, wint_t (*real_function)(FILE *)) {
    init_syms();
    if (!real_function) real_function = real_fgetwc_fn;
    if (!real_function) {
        errno = ENOSYS;
        return WEOF;
    }
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(file);
    wint_t result = WEOF;
    if (ensure_complete_locked(state, "wide_read") == 0) {
        reentry++;
        result = real_function(file);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

#undef getwc
#undef getwchar
#undef getwc_unlocked
#undef getwchar_unlocked

int model_fgetwc(FILE *file) __asm__("fgetwc");
int model_fgetwc(FILE *file) {
    return fgetwc_managed(file, real_fgetwc_fn);
}

int model_getwc(FILE *file) __asm__("getwc");
int model_getwc(FILE *file) {
    return fgetwc_managed(file, real_getwc_fn);
}

int model_getwchar(void) __asm__("getwchar");
int model_getwchar(void) {
    return model_fgetwc(stdin);
}

int model_fgetwc_unlocked(FILE *file) __asm__("fgetwc_unlocked");
int model_fgetwc_unlocked(FILE *file) {
    init_syms();
    return fgetwc_managed(
        file,
        real_fgetwc_unlocked_fn ? real_fgetwc_unlocked_fn : real_fgetwc_fn);
}

int model_getwc_unlocked(FILE *file) __asm__("getwc_unlocked");
int model_getwc_unlocked(FILE *file) {
    init_syms();
    return fgetwc_managed(
        file,
        real_getwc_unlocked_fn ? real_getwc_unlocked_fn : real_fgetwc_unlocked_fn);
}

int model_getwchar_unlocked(void) __asm__("getwchar_unlocked");
int model_getwchar_unlocked(void) {
    return model_fgetwc_unlocked(stdin);
}

static wchar_t *fgetws_managed(
    wchar_t *buffer,
    int size,
    FILE *file,
    wchar_t *(*real_function)(wchar_t *, int, FILE *)) {
    init_syms();
    if (!real_function) real_function = real_fgetws_fn;
    if (!real_function) {
        errno = ENOSYS;
        return NULL;
    }
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(buffer, size, file);
    wchar_t *result = NULL;
    if (ensure_complete_locked(state, "wide_read") == 0) {
        reentry++;
        result = real_function(buffer, size, file);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

wchar_t *model_fgetws(wchar_t *buffer, int size, FILE *file) __asm__("fgetws");
wchar_t *model_fgetws(wchar_t *buffer, int size, FILE *file) {
    return fgetws_managed(buffer, size, file, real_fgetws_fn);
}

wchar_t *model_fgetws_unlocked(wchar_t *buffer, int size, FILE *file) __asm__("fgetws_unlocked");
wchar_t *model_fgetws_unlocked(wchar_t *buffer, int size, FILE *file) {
    init_syms();
    return fgetws_managed(
        buffer,
        size,
        file,
        real_fgetws_unlocked_fn ? real_fgetws_unlocked_fn : real_fgetws_fn);
}

static wchar_t *fgetws_chk_managed(
    wchar_t *buffer,
    size_t buffer_length,
    int size,
    FILE *file,
    wchar_t *(*real_function)(wchar_t *, size_t, int, FILE *)) {
    init_syms();
    if (!real_function) {
        errno = ENOSYS;
        return NULL;
    }
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(buffer, buffer_length, size, file);
    wchar_t *result = NULL;
    if (ensure_complete_locked(state, "wide_read") == 0) {
        reentry++;
        result = real_function(buffer, buffer_length, size, file);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

wchar_t *__fgetws_chk(wchar_t *buffer, size_t buffer_length, int size, FILE *file) {
    init_syms();
    return fgetws_chk_managed(buffer, buffer_length, size, file, real___fgetws_chk_fn);
}

wchar_t *__fgetws_unlocked_chk(wchar_t *buffer, size_t buffer_length, int size, FILE *file) {
    init_syms();
    return fgetws_chk_managed(
        buffer,
        buffer_length,
        size,
        file,
        real___fgetws_unlocked_chk_fn);
}

static int vfwscanf_managed(
    FILE *file,
    const wchar_t *format,
    va_list arguments,
    int (*real_function)(FILE *, const wchar_t *, va_list)) {
    init_syms();
    if (!real_function) {
        errno = ENOSYS;
        return EOF;
    }
    shared_state_t *state = acquire_stream_state_for_io(file);
    if (!state) return real_function(file, format, arguments);
    int result = EOF;
    if (ensure_complete_locked(state, "wide_formatted_read") == 0) {
        reentry++;
        result = real_function(file, format, arguments);
        reentry--;
    }
    else {
        mark_stdio_error(file);
    }
    release_stream_state_after_io(file, state);
    return result;
}

int model_vfwscanf(FILE *file, const wchar_t *format, va_list arguments) __asm__("vfwscanf");
int model_vfwscanf(FILE *file, const wchar_t *format, va_list arguments) {
    init_syms();
    return vfwscanf_managed(file, format, arguments, real_vfwscanf_fn);
}

int model_fwscanf(FILE *file, const wchar_t *format, ...) __asm__("fwscanf");
int model_fwscanf(FILE *file, const wchar_t *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfwscanf_managed(file, format, arguments, real_vfwscanf_fn);
    va_end(arguments);
    return result;
}

int model_isoc99_vfwscanf(FILE *file, const wchar_t *format, va_list arguments)
    __asm__("__isoc99_vfwscanf");
int model_isoc99_vfwscanf(FILE *file, const wchar_t *format, va_list arguments) {
    init_syms();
    return vfwscanf_managed(
        file,
        format,
        arguments,
        real___isoc99_vfwscanf_fn ? real___isoc99_vfwscanf_fn : real_vfwscanf_fn);
}

int model_isoc99_fwscanf(FILE *file, const wchar_t *format, ...) __asm__("__isoc99_fwscanf");
int model_isoc99_fwscanf(FILE *file, const wchar_t *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfwscanf_managed(
        file,
        format,
        arguments,
        real___isoc99_vfwscanf_fn ? real___isoc99_vfwscanf_fn : real_vfwscanf_fn);
    va_end(arguments);
    return result;
}

int model_isoc23_vfwscanf(FILE *file, const wchar_t *format, va_list arguments)
    __asm__("__isoc23_vfwscanf");
int model_isoc23_vfwscanf(FILE *file, const wchar_t *format, va_list arguments) {
    init_syms();
    return vfwscanf_managed(
        file,
        format,
        arguments,
        real___isoc23_vfwscanf_fn
            ? real___isoc23_vfwscanf_fn
            : (real___isoc99_vfwscanf_fn ? real___isoc99_vfwscanf_fn : real_vfwscanf_fn));
}

int model_isoc23_fwscanf(FILE *file, const wchar_t *format, ...) __asm__("__isoc23_fwscanf");
int model_isoc23_fwscanf(FILE *file, const wchar_t *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfwscanf_managed(
        file,
        format,
        arguments,
        real___isoc23_vfwscanf_fn
            ? real___isoc23_vfwscanf_fn
            : (real___isoc99_vfwscanf_fn ? real___isoc99_vfwscanf_fn : real_vfwscanf_fn));
    va_end(arguments);
    return result;
}

int model_vwscanf(const wchar_t *format, va_list arguments) __asm__("vwscanf");
int model_vwscanf(const wchar_t *format, va_list arguments) {
    init_syms();
    return vfwscanf_managed(stdin, format, arguments, real_vfwscanf_fn);
}

int model_wscanf(const wchar_t *format, ...) __asm__("wscanf");
int model_wscanf(const wchar_t *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfwscanf_managed(stdin, format, arguments, real_vfwscanf_fn);
    va_end(arguments);
    return result;
}

int model_isoc99_vwscanf(const wchar_t *format, va_list arguments) __asm__("__isoc99_vwscanf");
int model_isoc99_vwscanf(const wchar_t *format, va_list arguments) {
    init_syms();
    return vfwscanf_managed(
        stdin,
        format,
        arguments,
        real___isoc99_vfwscanf_fn ? real___isoc99_vfwscanf_fn : real_vfwscanf_fn);
}

int model_isoc99_wscanf(const wchar_t *format, ...) __asm__("__isoc99_wscanf");
int model_isoc99_wscanf(const wchar_t *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfwscanf_managed(
        stdin,
        format,
        arguments,
        real___isoc99_vfwscanf_fn ? real___isoc99_vfwscanf_fn : real_vfwscanf_fn);
    va_end(arguments);
    return result;
}

int model_isoc23_vwscanf(const wchar_t *format, va_list arguments) __asm__("__isoc23_vwscanf");
int model_isoc23_vwscanf(const wchar_t *format, va_list arguments) {
    init_syms();
    return vfwscanf_managed(
        stdin,
        format,
        arguments,
        real___isoc23_vfwscanf_fn
            ? real___isoc23_vfwscanf_fn
            : (real___isoc99_vfwscanf_fn ? real___isoc99_vfwscanf_fn : real_vfwscanf_fn));
}

int model_isoc23_wscanf(const wchar_t *format, ...) __asm__("__isoc23_wscanf");
int model_isoc23_wscanf(const wchar_t *format, ...) {
    init_syms();
    va_list arguments;
    va_start(arguments, format);
    int result = vfwscanf_managed(
        stdin,
        format,
        arguments,
        real___isoc23_vfwscanf_fn
            ? real___isoc23_vfwscanf_fn
            : (real___isoc99_vfwscanf_fn ? real___isoc99_vfwscanf_fn : real_vfwscanf_fn));
    va_end(arguments);
    return result;
}

void *mmap(void *address, size_t length, int protection, int flags, int fd, off_t offset) {
    init_syms();
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_mmap_fn(address, length, protection, flags, fd, offset);
    void *result = MAP_FAILED;
    if (ensure_complete_locked(state, "mapping") == 0) {
        result = real_mmap_fn(address, length, protection, flags, fd, offset);
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

void *mmap64(void *address, size_t length, int protection, int flags, int fd, off64_t offset) {
    init_syms();
    if (!real_mmap64_fn) return mmap(address, length, protection, flags, fd, (off_t)offset);
    shared_state_t *state = acquire_fd_state_for_io(fd);
    if (!state) return real_mmap64_fn(address, length, protection, flags, fd, offset);
    void *result = MAP_FAILED;
    if (ensure_complete_locked(state, "mapping") == 0) {
        result = real_mmap64_fn(address, length, protection, flags, fd, offset);
    }
    pthread_mutex_unlock(&state->io_lock);
    state_release_reference(state);
    return result;
}

int access(const char *pathname, int mode) {
    return faccessat(AT_FDCWD, pathname, mode, 0);
}

int faccessat(int directory_fd, const char *pathname, int mode, int flags) {
    init_syms();
    if (!real_faccessat_fn) return syscall(SYS_faccessat, directory_fd, pathname, mode, flags);
    return real_faccessat_fn(directory_fd, pathname, mode, flags);
}

int statx(int directory_fd, const char *pathname, int flags, unsigned int mask, struct statx *buffer) {
    init_syms();
    if (!real_statx_fn) {
        errno = ENOSYS;
        return -1;
    }
    return real_statx_fn(directory_fd, pathname, flags, mask, buffer);
}
