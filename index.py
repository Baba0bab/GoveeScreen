import tkinter as tk
import threading
import time
import mss
from PIL import Image, ImageStat
import socket
import json

DEVICE_IP = "192.168.1.175"
DELAY = 0.03333333

running = True
paused = False
brightness_level = 2
last_color = (0, 0, 0)
last_brightness = 0
current_monitor_index = 1
total_monitors = 1

def send_govee_color(r, g, b, brightness_percent):
    color_payload = {
        "msg": {
            "cmd": "colorwc",
            "data": {
                "color": {"r": r, "g": g, "b": b},
                "colorTemInKelvin": 0
            }
        }
    }
    brightness_payload = {
        "msg": {
            "cmd": "brightness",
            "data": {
                "value": int(brightness_percent)
            }
        }
    }
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(1.0)
        sock.sendto(json.dumps(color_payload).encode(), (DEVICE_IP, 4003))
        sock.sendto(json.dumps(brightness_payload).encode(), (DEVICE_IP, 4003))
        sock.close()
    except Exception as e:
        print(f"Erreur lors de l'envoi au Govee: {e}")

def is_black_bar(pixels, threshold=20, tolerance=0.98):
    total = len(pixels)
    dark = sum(1 for r, g, b in pixels if r < threshold and g < threshold and b < threshold)
    return (dark / total) > tolerance

def get_average_color(monitor_index):
    with mss.mss() as sct:
        monitor = sct.monitors[monitor_index]
        width, height = monitor["width"], monitor["height"]

        top_bar = {"top": monitor["top"], "left": monitor["left"], "width": width, "height": 40}
        bottom_bar = {"top": monitor["top"] + height - 40, "left": monitor["left"], "width": width, "height": 40}

        top_img = Image.frombytes("RGB", sct.grab(top_bar).size, sct.grab(top_bar).rgb)
        bottom_img = Image.frombytes("RGB", sct.grab(bottom_bar).size, sct.grab(bottom_bar).rgb)

        top_pixels = list(top_img.getdata())
        bottom_pixels = list(bottom_img.getdata())

        skip_top = is_black_bar(top_pixels)
        skip_bottom = is_black_bar(bottom_pixels)

        top_offset = 80 if skip_top else 0
        bottom_offset = 80 if skip_bottom else 0

        main_height = height - top_offset - bottom_offset
        screen_region = {
            "top": monitor["top"] + top_offset,
            "left": monitor["left"],
            "width": width,
            "height": main_height
        }

        sct_img = sct.grab(screen_region)
        img = Image.frombytes("RGB", sct_img.size, sct_img.rgb)

        resized_img = img.resize((32, 18))
        stat = ImageStat.Stat(resized_img)
        avg_r, avg_g, avg_b = map(int, stat.mean[:3])

        brightness = (0.2126 * avg_r + 0.7152 * avg_g + 0.0722 * avg_b) / 255 * 100
        if brightness_level == 1:
            brightness *= 0.5
        elif brightness_level == 3:
            brightness *= 2
        elif brightness_level == 4:
            brightness = 100
        brightness = max(0, min(100, brightness))

        return avg_r, avg_g, avg_b, brightness

def update_loop(label, status_label, monitor_label):
    global last_color, last_brightness, running, paused, current_monitor_index
    while running:
        if paused:
            status_label.config(text="⏸ Pause", bg="#606060")
            time.sleep(0.1)
            continue
        try:
            current_color = get_average_color(current_monitor_index)
        except Exception as e:
            label.config(text="Erreur capture", bg="#FF4040")
            time.sleep(1)
            continue

        r, g, b, br = current_color
        if (r, g, b) != last_color or abs(br - last_brightness) > 1:
            send_govee_color(r, g, b, br)
            last_color = (r, g, b)
            last_brightness = br
            color_hex = f"#{r:02x}{g:02x}{b:02x}"
            label.config(text=f"Couleur: {r}, {g}, {b}\nLum: {br:.1f}%", bg=color_hex)
            status_label.config(text="Actif", bg="#228B22")
            monitor_label.config(text=f"Écran actuel : {current_monitor_index}")
        time.sleep(DELAY)

def on_close(window):
    global running
    running = False
    send_govee_color(0, 0, 0, 0)
    window.destroy()

def toggle_pause(pause_button):
    global paused
    paused = not paused
    pause_button.config(text="▶ Reprendre" if paused else "⏸ Pause")

def change_monitor(monitor_label):
    global current_monitor_index, total_monitors
    current_monitor_index += 1
    if current_monitor_index > total_monitors:
        current_monitor_index = 1
    monitor_label.config(text=f"Écran actuel : {current_monitor_index}")

def main_gui():
    global total_monitors, brightness_level
    total_monitors = len(mss.mss().monitors) - 1

    window = tk.Tk()
    window.title("Contrôle Govee")
    window.geometry("340x240")
    window.resizable(False, False)
    window.configure(bg="#2B2B2B")

    label = tk.Label(window, text="Démarrage...", font=("Arial", 12), bg="#2B2B2B", fg="#E0E0E0", height=2, pady=10)
    label.pack(fill="x", padx=0, pady=0)

    status_label = tk.Label(window, text="🟡 En attente...", font=("Arial", 10), bg="#FFA500", fg="#FFFFFF")
    status_label.pack(fill="x", padx=30, pady=5)

    monitor_label = tk.Label(window, text=f"Écran actuel : {current_monitor_index}", font=("Arial", 10), bg="#2B2B2B", fg="#E0E0E0")
    monitor_label.pack(pady=5)

    brightness_label = tk.Label(window, text="Niveau de luminosité", font=("Arial", 10), bg="#2B2B2B", fg="#E0E0E0")
    brightness_label.pack(pady=5)

    brightness_scale = tk.Scale(
        window,
        from_=1,
        to=4,
        orient=tk.HORIZONTAL,
        length=200,
        showvalue=0,
        tickinterval=1,
        bg="#3C3F41",
        fg="#E0E0E0",
        troughcolor="#4C4C4C",
        highlightthickness=0,
        command=lambda value: set_brightness_level(int(value))
    )
    brightness_scale.set(brightness_level)
    brightness_scale.pack(pady=5)

    def set_brightness_level(value):
        global brightness_level
        brightness_level = int(value)

    btn_frame = tk.Frame(window, bg="#2B2B2B")
    btn_frame.pack(pady=5)

    pause_button = tk.Button(btn_frame, text="⏸ Pause", command=lambda: toggle_pause(pause_button), width=10, bg="#3C3F41", fg="#FFFFFF", activebackground="#4C4C4C")
    pause_button.grid(row=0, column=0, padx=5)

    monitor_button = tk.Button(btn_frame, text="Changer d'écran", command=lambda: change_monitor(monitor_label), width=12, bg="#3C3F41", fg="#FFFFFF", activebackground="#4C4C4C")
    monitor_button.grid(row=0, column=1, padx=5)

    threading.Thread(target=update_loop, args=(label, status_label, monitor_label), daemon=True).start()
    window.protocol("WM_DELETE_WINDOW", lambda: on_close(window))
    window.mainloop()

if __name__ == "__main__":
    main_gui()