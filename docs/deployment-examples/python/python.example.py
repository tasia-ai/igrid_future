import configparser
import subprocess
import socket
import os
import time
import threading

# Load configuration from INI file
config = configparser.ConfigParser()
config_path = '/home/grid/opensim/bin/python_config.ini'
if not os.path.exists(config_path):
    raise FileNotFoundError(f"Config file not found: {config_path}")
config.read(config_path)

processes = {}

# Utility to check if a tmux session is running
def is_tmux_running(session_name):
    try:
        output = subprocess.check_output(['tmux', 'list-sessions']).decode('utf-8')
        return session_name in output
    except subprocess.CalledProcessError:
        return False

# Send 'q' and Enter to a tmux session
def send_q_to_tmux(session_name):
    try:
        subprocess.run(['tmux', 'send-keys', '-t', session_name, 'q', 'Enter'], check=True)
        print(f"Sent 'q' followed by Enter to tmux session: {session_name}")
    except subprocess.CalledProcessError as e:
        print(f"Error sending 'q' to tmux session {session_name}: {e}")

# Check if tmux session is running
def is_tmux_session_running(session_name):
    try:
        result = subprocess.run(['tmux', 'has-session', '-t', session_name],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return result.returncode == 0
    except Exception as e:
        print(f"Error checking tmux session: {e}")
        return False

# Kill tmux session
def kill_tmux_session(session_name):
    try:
        subprocess.run(['tmux', 'kill-session', '-t', session_name], check=True)
        print(f"Killed tmux session: {session_name}")
    except subprocess.CalledProcessError as e:
        print(f"Error killing tmux session {session_name}: {e}")

# Start a program (NO PORT CHECKING)
def start_program(name, cmd, use_pid_file=False, session_name=None):

    # Prevent duplicates
    if session_name and is_tmux_running(session_name):
        print(f"{name} already running in tmux session: {session_name}")
        return None

    if session_name:
        cmd = f"tmux new-session -d -s {session_name} {cmd}"

    process = subprocess.Popen(cmd, shell=True)

    if use_pid_file:
        with open(f'{name}.pid', 'w') as f:
            f.write(str(process.pid))

    return process

# Monitor & auto-restart
def monitor_program(name, cmd, use_pid_file=False, session_name=None):
    process = start_program(name, cmd, use_pid_file, session_name)
    if process:
        processes[name] = process

    while True:
        try:
            if session_name:
                if not is_tmux_running(session_name):
                    print(f"{name} tmux session not found, restarting...")
                    process = start_program(name, cmd, use_pid_file, session_name)
                    processes[name] = process
            else:
                if process.poll() is not None:
                    print(f"{name} crashed, restarting...")
                    process = start_program(name, cmd, use_pid_file, session_name)
                    processes[name] = process

            time.sleep(60)

        except Exception as e:
            print(f"Error monitoring {name}: {e}")

# --------------------
# MAIN (NO WEB PANEL)
# --------------------

if __name__ == "__main__":

    for program_name, cmd in config['programs'].items():
        if config['enable'].getboolean(program_name, fallback=False):

            use_pid_file = config['settings'].getboolean('use_pid_file', fallback=False)
            session_name = config['session_names'].get(program_name)

            threading.Thread(
                target=monitor_program,
                args=(program_name, cmd, use_pid_file, session_name)
            ).start()

        else:
            print(f"{program_name} management disabled in config.")
