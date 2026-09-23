"""Оформление страховой и несколько вещей, повторяющихся в каждой форме.

Цвета, шрифты, карточки, таблицы и мелкие элементы (степпер, кнопки, обзор
покрытий) спрятаны здесь, чтобы потом не писать их заново в каждом месте app.py.
"""
import tkinter as tk
from tkinter import ttk

BACKGROUND = "#eff5f4"
SURFACE = "#ffffff"
INK = "#123633"
MUTED = "#647a79"
ACCENT = "#0f766e"
MINT = "#6ee7b7"
AMBER = "#d97706"
RED = "#dc2626"
HEADER = "#123633"
HEADER_SOFT = "#1f4a47"
BORDER = "#e8f0ef"
FIELD_BG = "#eff5f4"
STEEL = "#d1e8e5"
BANNER = "#f0fdf9"
BANNER_BAR = "#d1fae5"


def configure(window):
    window.configure(background=BACKGROUND)
    window.option_add("*Font", ("Segoe UI", 10))
    style = ttk.Style(window)
    style.theme_use("clam")
    style.configure(".", font=("Segoe UI", 10), background=SURFACE, foreground=INK)
    style.configure("Page.TFrame", background=BACKGROUND)
    style.configure("Caps.TLabel", font=("Segoe UI", 9, "bold"), foreground=MUTED)
    style.configure("Muted.TLabel", foreground=MUTED)
    style.configure("Title.TLabel", font=("Segoe UI", 14, "bold"), foreground=INK)
    style.configure("Metric.TLabel", font=("Segoe UI", 20, "bold"))
    style.configure("TButton", padding=(12, 7), background="#e6efed", borderwidth=0)
    style.map("TButton", background=[("active", "#d3e4df")])
    style.configure("Primary.TButton", background=ACCENT, foreground="white")
    style.map("Primary.TButton", background=[("active", "#115e59"), ("disabled", "#94a3a0")])
    style.configure("Danger.TButton", background="#fff1f2", foreground="#be123c")
    style.map("Danger.TButton", background=[("active", "#ffe4e6")])
    style.configure("TEntry", padding=6, fieldbackground="#ffffff", bordercolor=STEEL)
    style.configure("TCombobox", padding=6, fieldbackground="#ffffff", arrowsize=14)
    style.map("TCombobox", fieldbackground=[("readonly", "#ffffff")])
    style.configure("TCheckbutton", padding=5, background=SURFACE)
    style.map("TCheckbutton", background=[("active", "#f0f7f6")])
    style.configure("TNotebook", background=BACKGROUND, borderwidth=0, tabmargins=(0, 0, 0, 4))
    style.configure("TNotebook.Tab", padding=(12, 7), background="#e0ebe7", foreground=MUTED,
                    font=("Segoe UI", 9, "bold"))
    style.map("TNotebook.Tab", background=[("selected", SURFACE)], foreground=[("selected", ACCENT)])
    style.configure("Treeview", rowheight=28, borderwidth=0, fieldbackground=SURFACE)
    style.configure("Treeview.Heading", background="#eff5f4", padding=7, font=("Segoe UI", 9, "bold"),
                    relief="flat")
    style.map("Treeview", background=[("selected", "#ccede2")], foreground=[("selected", "#134e4a")])
    style.configure("Horizontal.TProgressbar", background=ACCENT, troughcolor=BANNER_BAR,
                    borderwidth=0, lightcolor=ACCENT, darkcolor=ACCENT)


def card(parent, padding=12, grid=None, **pack):
    """Белая карточка с тонкой рамкой по дизайну."""
    outer = tk.Frame(parent, background=SURFACE, highlightbackground=BORDER, highlightthickness=1)
    if grid:
        outer.grid(**grid)
    else:
        pack.setdefault("fill", "x")
        outer.pack(**pack)
    if isinstance(padding, tuple):
        horizontal, vertical = padding
    else:
        horizontal = vertical = padding
    body = tk.Frame(outer, background=SURFACE)
    body.pack(fill="both", expand=True, padx=horizontal, pady=vertical)
    return body


def card_header(parent, title, color=ACCENT, count_text=None):
    """Шапка карточки: цветная точка, название и необязательное число справа."""
    header = tk.Frame(parent, background=SURFACE)
    header.pack(fill="x", pady=(0, 8))
    canvas = tk.Canvas(header, width=8, height=8, background=SURFACE, highlightthickness=0)
    canvas.pack(side="left", pady=(2, 0))
    canvas.create_oval(1, 1, 7, 7, fill=color, outline="")
    title_label = tk.Label(header, text=title, background=SURFACE, foreground=INK,
                           font=("Segoe UI", 12, "bold"))
    title_label.pack(side="left", padx=(8, 0))
    header.title_label = title_label
    if count_text is not None:
        count_label = ttk.Label(header, text=count_text, style="Muted.TLabel")
        count_label.pack(side="right")
        header.count_label = count_label
    return header


def field(parent, label, value="", width=16):
    group = ttk.Frame(parent)
    group.pack(side="left", padx=(0, 12), anchor="s")
    ttk.Label(group, text=label, style="Caps.TLabel").pack(anchor="w", pady=(0, 2))
    entry = ttk.Entry(group, width=width)
    entry.insert(0, value)
    entry.pack(fill="x")
    return entry


def outline_button(parent, text, color=ACCENT, command=None):
    return tk.Button(parent, text=text, command=command, cursor="hand2", padx=10, pady=4,
                     background=SURFACE, foreground=color, activebackground="#f0f7f6",
                     activeforeground=color, relief="solid", bd=1, highlightthickness=0,
                     highlightbackground=color, font=("Segoe UI", 9, "bold"))


def solid_button(parent, text, color=ACCENT, foreground="white", command=None):
    return tk.Button(parent, text=text, command=command, cursor="hand2", padx=12, pady=5,
                     background=color, foreground=foreground, activebackground=color,
                     activeforeground=foreground, relief="flat", bd=0, highlightthickness=0,
                     font=("Segoe UI", 9, "bold"))


def stepper(parent, minimum, maximum, value, width=7):
    """Переключатель − значение +; возвращает объект с get() как у entry."""
    frame = tk.Frame(parent, background=SURFACE)
    frame.pack(side="left", padx=(0, 12), anchor="s")
    var = tk.StringVar(value=str(value))
    box = tk.Frame(frame, background=STEEL, bd=0, highlightthickness=1, highlightbackground=STEEL)
    box.pack(fill="x")

    def step(delta):
        try:
            number = int(var.get())
        except ValueError:
            number = minimum
        number = max(minimum, min(maximum, number + delta))
        var.set(str(number))

    tk.Button(box, text="−", command=lambda: step(-1), cursor="hand2", width=2, relief="flat", bd=0,
              background=SURFACE, foreground=MUTED, activebackground="#f0f7f6",
              activeforeground=INK, font=("Segoe UI", 11, "bold")).pack(side="left", padx=2)
    ttk.Entry(box, textvariable=var, width=width, justify="center").pack(side="left", ipady=3)
    tk.Button(box, text="+", command=lambda: step(1), cursor="hand2", width=2, relief="flat", bd=0,
              background=SURFACE, foreground=MUTED, activebackground="#f0f7f6",
              activeforeground=INK, font=("Segoe UI", 11, "bold")).pack(side="left", padx=2)
    return var


def dot(parent, color=MINT, size=6):
    canvas = tk.Canvas(parent, width=size, height=size, highlightthickness=0)
    canvas.pack(side="left", padx=(0, 8), pady=(0, 0))
    canvas.create_oval(0, 0, size, size, fill=color, outline=color)
    return canvas


def metric(parent, title, column, accent=ACCENT):
    parent.columnconfigure(column, weight=1, uniform="metrics")
    card_frame = tk.Frame(parent, background=SURFACE, highlightbackground=BORDER, highlightthickness=1)
    card_frame.grid(row=0, column=column, sticky="nsew", padx=(0, 12 if column < 2 else 0))
    ttk.Label(card_frame, text=title, style="Caps.TLabel").pack(anchor="w", padx=16, pady=(10, 2))
    value = tk.StringVar(value="—")
    tk.Label(card_frame, textvariable=value, background=SURFACE, foreground=accent,
             font=("Segoe UI", 22, "bold"), anchor="w").pack(fill="x", padx=16, pady=(0, 10))
    return value


def table(parent, columns):
    frame = ttk.Frame(parent)
    frame.pack(fill="both", expand=True, pady=8)
    frame.rowconfigure(0, weight=1)
    frame.columnconfigure(0, weight=1)
    tree = ttk.Treeview(frame, columns=columns, show="headings", selectmode="browse", height=5)
    for column in columns:
        tree.heading(column, text=column, anchor="w")
        tree.column(column, width=125 if column != "ID" else 55, minwidth=55)
    tree.grid(row=0, column=0, sticky="nsew")
    vertical = ttk.Scrollbar(frame, command=tree.yview)
    vertical.grid(row=0, column=1, sticky="ns")
    horizontal = ttk.Scrollbar(frame, orient="horizontal", command=tree.xview)
    horizontal.grid(row=1, column=0, sticky="ew")
    tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
    for tag, color in (("success", "#0f766e"), ("pending", "#a16207"),
                       ("closed", "#738481"), ("rejected", "#be123c")):
        tree.tag_configure(tag, foreground=color)
    tree.tag_configure("even", background="#f4f9f6")
    tree.empty_label = ttk.Label(frame, text="Zatím zde nejsou žádné záznamy.", style="Muted.TLabel")
    tree.empty_label.place(relx=0.5, rely=0.55, anchor="center")
    return tree


def sink_text(parent):
    """Поле в виде описания предложения: светло-серая сплошная рамка."""
    box = tk.Frame(parent, background=FIELD_BG, padx=12, pady=8)
    box.pack(fill="x", pady=(0, 8), anchor="n")
    label = tk.Label(box, background=FIELD_BG, foreground=MUTED, wraplength=760, justify="left",
                     anchor="w", font=("Segoe UI", 10))
    label.pack(fill="x")
    return label


def scroll_form(parent):
    """Длинная форма остаётся доступной и на маленьком мониторе."""
    container = ttk.Frame(parent)
    container.pack(fill="both", expand=True)
    canvas = tk.Canvas(container, background=SURFACE, highlightthickness=0)
    scrollbar = ttk.Scrollbar(container, command=canvas.yview)
    scrollbar.pack(side="right", fill="y")
    canvas.pack(side="left", fill="both", expand=True)
    canvas.configure(yscrollcommand=scrollbar.set)
    form = ttk.Frame(canvas, padding=6)
    item = canvas.create_window((0, 0), window=form, anchor="nw")
    form.bind("<Configure>", lambda _: canvas.configure(scrollregion=canvas.bbox("all")))
    canvas.bind("<Configure>", lambda event: canvas.itemconfigure(item, width=event.width))

    def wheel(event):
        hovered = canvas.winfo_containing(event.x_root, event.y_root)
        while hovered is not None:
            if hovered == canvas:
                if form.winfo_height() > canvas.winfo_height():
                    canvas.yview_scroll(-1 if event.delta > 0 else 1, "units")
                return
            hovered = getattr(hovered, "master", None)

    parent.winfo_toplevel().bind("<MouseWheel>", wheel, add="+")
    return form


def text_area(parent):
    """Тёмная панель с записями аудита, как её рисует макет."""
    frame = ttk.Frame(parent)
    frame.pack(fill="both", expand=True)
    text = tk.Text(frame, wrap="word", font=("Consolas", 10), background=HEADER, foreground="#cfe3dd",
                   relief="flat", padx=12, pady=12, insertbackground="white")
    text.pack(side="left", fill="both", expand=True)
    scrollbar = ttk.Scrollbar(frame, command=text.yview)
    scrollbar.pack(side="right", fill="y")
    text.configure(yscrollcommand=scrollbar.set)
    return text


def covered_row(parent, text, background=SURFACE):
    """Строка покрытия: зелёная точка с галочкой и название пункта."""
    row = tk.Frame(parent, background=background)
    row.pack(fill="x", pady=3)
    dot_canvas = tk.Canvas(row, width=18, height=18, background=background, highlightthickness=0)
    dot_canvas.pack(side="left")
    dot_canvas.create_oval(1, 1, 17, 17, fill=ACCENT, outline="")
    dot_canvas.create_line(5, 9, 8, 12, 14, 5, fill="white", width=2, capstyle="round")
    tk.Label(row, text=text, background=background, foreground=INK).pack(side="left", padx=(8, 0))
    return row