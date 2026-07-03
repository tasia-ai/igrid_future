import configparser
import subprocess
import os
import time
import threading

CONFIG_PATH = '/home/grid/opensim/bin/python_config.ini'

# Load configuration
config = configparser.ConfigParser()
if not os.path.exists(CONFIG_PATH):
    raise FileNotFoundError(f"Config file not found: {CONFIG_PATH}")

config.read(CONFIG_PATH)

if 'programs' not in config or 'enable' not in config:
    raise KeyError("Config must contain [programs] and [enable] sections")

# --------------------------------------------------------
# Check if dotnet process (DLL) is running
# --------------------------------------------------------
def is_dotnet_process_running(cmd: str) -> bool:
    """
    From the command (e.g. 'dotnet Robust.dll'), extract the first *.dll
    and check if that DLL name appears in the output of `ps aux`.
    """
    try:
        parts = cmd.split()
        dll_name = None

        for part in parts:
            if part.endswith(".dll"):
                dll_name = os.path.basename(part)
                break

        if not dll_name:
            print(f"[WARN] No .dll found in command, cannot monitor: {cmd}")
            return False

        ps_output = subprocess.check_output(["ps", "aux"], text=True)
        running = dll_name in ps_output

        # Debug info
        print(f"[CHECK] {dll_name}: {'RUNNING' if running else 'NOT RUNNING'}")
        return running

    except Exception as e:
        print(f"[ERROR] While checking dotnet process for '{cmd}': {e}")
        return False


# --------------------------------------------------------
# tmux helpers
# --------------------------------------------------------
def is_tmux_running(session_name: str) -> bool:
    try:
        output = subprocess.check_output(['tmux', 'list-sessions'], text=True)
        return session_name in output
    except subprocess.CalledProcessError:
        return False


def kill_tmux_session(session_name: str) -> None:
    try:
        subprocess.run(['tmux', 'kill-session', '-t', session_name], check=True)
        print(f"[INFO] Killed tmux session: {session_name}")
    except subprocess.CalledProcessError:
        print(f"[INFO] tmux session '{session_name}' not found to kill.")


# --------------------------------------------------------
# Start program in tmux
# --------------------------------------------------------
def start_program(name: str, cmd: str):
    session_name = name  # use program name as tmux session

    if is_tmux_running(session_name):
        print(f"[INFO] {name} already running in tmux session '{session_name}'.")
        return

    tmux_cmd = f"tmux new-session -d -s {session_name} {cmd}"
    print(f"[START] {name}: {tmux_cmd}")
    subprocess.Popen(tmux_cmd, shell=True)


# --------------------------------------------------------
# Monitor program
# --------------------------------------------------------
def monitor_program(name: str, cmd: str):
    session_name = name
    print(f"[MONITOR] Monitoring {name} (session '{session_name}')")

    # Initial start
    start_program(name, cmd)

    while True:
        try:
            if not is_dotnet_process_running(cmd):
                print(f"[MONITOR] {name} NOT running. Restarting...")

                # clean up tmux session if still present
                kill_tmux_session(session_name)

                # restart
                start_program(name, cmd)

            time.sleep(20)

        except Exception as e:
            print(f"[ERROR] Monitor for {name} failed: {e}")
            time.sleep(20)


# --------------------------------------------------------
# Main
# --------------------------------------------------------
if __name__ == "__main__":
    for program_name, cmd in config['programs'].items():
        enabled = config['enable'].getboolean(program_name, fallback=False)

        if not enabled:
            print(f"[INFO] {program_name} management disabled in config.")
            continue

        print(f"[INIT] Scheduling monitor for {program_name}: {cmd}")

        t = threading.Thread(
            target=monitor_program,
            args=(program_name, cmd),
            daemon=True
        )
        t.start()

    # keep main thread alive
    while True:
        time.sleep(3600)
