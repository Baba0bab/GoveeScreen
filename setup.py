from cx_Freeze import setup, Executable

executables = [Executable("index.py", base="Win32GUI", icon="votre_icone.ico")]
build_options = {"includes": ["tkinter", "mss", "PIL", "socket", "json"], "include_files": []}

setup(
    name="Controle Govee",
    version="1.0",
    description="Application pour contrôler les LED Govee",
    author="flash dev",
    options={"build_exe": build_options},
    executables=executables
)