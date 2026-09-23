"""Окно страховой в Tkinter.

Вся "мудрость" — расчёты, симулируемое время, продления, возмещения — находится
в C# (src/Insurance.App). Здесь лишь обёртка вокруг API: таблицы, формы и
отправка обратно. Само по себе это окно ничего не считает.
"""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import tkinter as tk
from tkinter import ttk, messagebox
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError

sys.path.insert(0, str(Path(__file__).resolve().parent))
import appearance

ROOT = Path(__file__).resolve().parents[2]
BASE = os.environ.get("INSURANCE_API_URL", "http://127.0.0.1:5090").rstrip("/")
# состояния из C# — у каждого свой код, здесь их названия переведены и окрашены
STATUS = {"Active": "Platná", "Scheduled": "Budoucí", "Expired": "Skončená", "Cancelled": "Ukončená",
          "AwaitingAdmin": "Čeká na správce", "AwaitingCustomer": "Návrh pro klienta", "Accepted": "Přijato",
          "Declined": "Odmítnuto", "Withdrawn": "Staženo", "Pending": "Čeká na rozhodnutí", "Paid": "Vyplaceno", "Rejected": "Zamítnuto"}
STATUS_TAGS = {"Active": "success", "Paid": "success", "Accepted": "success",
               "AwaitingAdmin": "pending", "AwaitingCustomer": "pending", "Pending": "pending",
               "Rejected": "rejected", "Cancelled": "closed", "Expired": "closed"}
# блок покрытий по виду страховки — ими наполняется предложение
COVERAGE = [
    ["Havarijní škody", "Živelné události"],
    ["Odcizení a vandalismus", "Rozšířená odpovědnost"],
    ["Asistence 24/7"],
]


def request(path, data=None):
    """Единственное место, которое говорит с сервером; возвращает dict или бросает исключение."""
    body = json.dumps(data).encode("utf-8") if data is not None else None
    try:
        with urlopen(Request(BASE + path, data=body, headers={"Content-Type": "application/json"}), timeout=10) as response:
            raw = response.read()
            return json.loads(raw) if raw else None
    except HTTPError as error:
        # сервер умеет присылать ошибки красиво в JSON, попробуем взять из него сообщение для того, кто смотрит
        text = error.read().decode("utf-8", errors="replace")
        try:
            text = json.loads(text).get("error", text)
        except ValueError:
            pass
        raise RuntimeError(f"Požadavek se nezdařil ({error.code}): {text}") from error
    except (URLError, TimeoutError) as error:
        raise RuntimeError("Server není dostupný. Spusťte jej nebo zkontrolujte připojení.") from error


def money(value):
    # C# присылает суммы с точкой и без пробелов; в таблицах хотим по-чешски (запятая + пробел)
    return f"{value:,.2f}".replace(",", " ").replace(".", ",") + " Kč"


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        appearance.configure(self)
        self.title("Pojišťovna | Smlouvy a pojistné události")
        self.geometry("1320x960")
        self.minsize(1160, 820)
        self.tasks = queue.Queue()
        self.busy = False
        self.server = None
        self.server_log = None
        self.data = {"policies": [], "offers": [], "claims": [], "customers": [], "audit": [], "today": ""}
        self.products = []
        self.role = "customer"
        self.date = tk.StringVar(value="Datum: nepřipojeno")
        self.status = tk.StringVar(value="Zvolte logger a spusťte server. Veškeré výpočty provádí C#.")
        self._build_header()
        metrics = ttk.Frame(self, style="Page.TFrame")
        metrics.pack(fill="x", padx=22, pady=8)
        self.policy_count = appearance.metric(metrics, "PLATNÉ SMLOUVY", 0, "#0f766e")
        self.offer_count = appearance.metric(metrics, "NABÍDKY K VYŘÍZENÍ", 1, "#d97706")
        self.claim_count = appearance.metric(metrics, "ČEKAJÍCÍ ŽÁDOSTI", 2, "#dc2626")
        self._build_controls()
        self.zone = ttk.Frame(self, style="Page.TFrame")
        self.zone.pack(fill="both", expand=True, padx=22, pady=(0, 8))
        self.customer_frame = ttk.Frame(self.zone, style="Page.TFrame")
        self.admin_frame = ttk.Frame(self.zone, style="Page.TFrame")
        self.build_client(self.customer_frame)
        self.build_admin(self.admin_frame)
        self._show_role("customer")
        self._build_footer()
        self.bind("<F5>", lambda _: self.guard(self.refresh))
        self.protocol("WM_DELETE_WINDOW", self.close)
        self.after(100, self.poll)

    def _build_header(self):
        header = tk.Frame(self, background=appearance.HEADER, padx=24, pady=10)
        header.pack(fill="x")
        brand = tk.Frame(header, background=appearance.HEADER)
        brand.pack(side="left")
        tk.Label(brand, text="Pojišťovna", background=appearance.HEADER, foreground="white",
                 font=("Segoe UI", 18, "bold")).pack(anchor="w")
        tk.Label(brand, text="AUTO A MAJETEK", background=appearance.HEADER,
                 foreground=appearance.MINT, font=("Segoe UI", 8, "bold")).pack(anchor="w")
        pills = tk.Frame(header, background=appearance.HEADER_SOFT, padx=4, pady=3)
        pills.pack(side="left", padx=(26, 0))
        self.role_buttons = {}
        for key, text in (("customer", "ZÁKAZNICKÁ ZÓNA"), ("admin", "SPRÁVA POJIŠŤOVNY")):
            button = tk.Button(pills, text=text, cursor="hand2", relief="flat", bd=0,
                               padx=14, pady=5, font=("Segoe UI", 10, "bold"),
                               command=lambda chosen=key: self._show_role(chosen))
            button.pack(side="left", padx=4)
            self.role_buttons[key] = button
        tk.Label(header, textvariable=self.date, background=appearance.HEADER,
                 foreground="#5b7573", font=("Segoe UI", 10)).pack(side="right")

    def _show_role(self, role):
        self.role = role
        for key, frame in (("customer", self.customer_frame), ("admin", self.admin_frame)):
            frame.pack_forget()
        self.customer_frame.pack_forget()
        self.admin_frame.pack_forget()
        (self.customer_frame if role == "customer" else self.admin_frame).pack(fill="both", expand=True)
        for key, button in self.role_buttons.items():
            if key == role:
                button.configure(background=appearance.MINT, foreground=appearance.INK,
                                 activebackground=appearance.MINT, activeforeground=appearance.INK)
            else:
                button.configure(background=appearance.HEADER_SOFT, foreground="#dfecea",
                                 activebackground=appearance.HEADER_SOFT, activeforeground="#ffffff")

    def _build_footer(self):
        self.progress = ttk.Progressbar(self, mode="indeterminate")
        self.progress.pack(fill="x", padx=22, pady=(8, 0))
        footer = tk.Frame(self, background=appearance.HEADER, padx=24, pady=8)
        footer.pack(fill="x")
        tk.Label(footer, textvariable=self.status, background=appearance.HEADER,
                 foreground="#9fb6b2", wraplength=700, justify="left").pack(side="left")
        right = tk.Frame(footer, background=appearance.HEADER)
        right.pack(side="right")
        tk.Label(right, text="Pojišťovna v2.0 – Auto a Majetek", background=appearance.HEADER,
                 foreground=appearance.MUTED, font=("Segoe UI", 9)).pack(side="left", padx=(0, 16))
        tk.Frame(right, background=appearance.HEADER, width=1, height=16).pack(side="left", padx=(0, 16))
        parent = tk.Frame(right, background=appearance.HEADER)
        parent.pack(side="left", padx=(0, 16))
        appearance.dot(parent, appearance.MINT)
        tk.Label(parent, text="Systém online", background=appearance.HEADER, foreground="#7fa19c",
                 font=("Segoe UI", 9)).pack(side="left")
        tk.Frame(right, background=appearance.HEADER, width=1, height=16).pack(side="left", padx=(0, 16))
        self.footer_active = tk.Label(right, text="0 aktivních smluv", background=appearance.HEADER,
                                      foreground="#7fa19c", font=("Segoe UI", 9))
        self.footer_active.pack(side="left", padx=(0, 16))
        tk.Frame(right, background=appearance.HEADER, width=1, height=16).pack(side="left", padx=(0, 16))
        self.footer_premium = tk.Label(right, text="Celkem pojistné: 0 Kč/rok",
                                       background=appearance.HEADER, foreground="#9fb6b2",
                                       font=("Segoe UI", 9, "bold"))
        self.footer_premium.pack(side="left")

        self.after(60, self._maximize)

    def _build_controls(self):
        controls = ttk.Frame(self, padding=(22, 0, 22, 6))
        controls.pack(fill="x")
        ttk.Label(controls, text="Zápis událostí", style="Muted.TLabel").pack(side="left", padx=(0, 10))
        self.logger = ttk.Combobox(controls, values=["file", "console", "both"], state="readonly", width=10)
        self.logger.set("file")
        self.logger.pack(side="left", padx=8)
        ttk.Label(controls, text="Soubor / konzole / obojí · volba před spuštěním",
                  style="Muted.TLabel").pack(side="left", padx=10)
        self.solid(controls, "Spustit server", self.start_server).pack(side="right")
        self.outline(controls, "Obnovit · F5", self.refresh).pack(side="right", padx=(0, 10))

    def solid(self, parent, text, action, color=appearance.ACCENT, foreground="white"):
        return appearance.solid_button(parent, text, color, foreground, lambda: self.guard(action))

    def outline(self, parent, text, action, color=appearance.ACCENT):
        return appearance.outline_button(parent, text, color, lambda: self.guard(action))

    def field(self, parent, label, value="", width=16):
        return appearance.field(parent, label, value, width)

    def row(self, parent):
        frame = ttk.Frame(parent)
        frame.pack(fill="x", pady=4)
        return frame

    def table(self, parent, columns):
        return appearance.table(parent, columns)

    @staticmethod
    def selected(tree):
        if not tree.selection():
            raise ValueError("Vyberte záznam v tabulce.")
        return int(tree.selection()[0])

    def guard(self, action):
        if self.busy:
            return
        try:
            action()
        except Exception as error:
            self.status.set(str(error))
            messagebox.showerror("Chyba", str(error), parent=self)

    def build_client(self, parent):
        bar = appearance.card(parent, padding=(12, 10))
        left = tk.Frame(bar, background=appearance.SURFACE)
        left.pack(side="left")
        tk.Label(left, text="ZÁKAZNÍK", background=appearance.SURFACE, foreground=appearance.MUTED,
                 font=("Segoe UI", 9, "bold")).pack(side="left", padx=(0, 10))
        self.customer = ttk.Combobox(left, state="readonly", width=24)
        self.customer.pack(side="left")
        self.customer.bind("<<ComboboxSelected>>", lambda _: self.render())
        self.customer_name = ttk.Entry(left, width=16)
        self.new_customer_visible = False
        self.new_button = self.outline(left, "+ Nový zákazník", self.toggle_customer_field)
        self.new_button.config(font=("Segoe UI", 9, "bold"))
        self.new_button.pack(side="left", padx=8)
        self.solid(left, "Registrovat", self.register).pack(side="left")
        tk.Frame(bar, background=appearance.BORDER, width=1, height=30).pack(side="left", padx=16)
        sim = tk.Frame(bar, background=appearance.SURFACE)
        sim.pack(side="left")
        tk.Label(sim, text="SIMULACE ČASU", background=appearance.SURFACE, foreground=appearance.MUTED,
                 font=("Segoe UI", 9, "bold")).pack(side="left", padx=(0, 10))
        self.sim_months = appearance.stepper(sim, 1, 120, 1)
        self.solid(sim, "Posunout čas", self.advance_time, appearance.INK).pack(side="left", padx=(10, 0))

        tabs = ttk.Notebook(parent)
        tabs.pack(fill="both", expand=True, pady=(8, 0))
        new = ttk.Frame(tabs, padding=8)
        policies = ttk.Frame(tabs, padding=8)
        offers = ttk.Frame(tabs, padding=8)
        claims = ttk.Frame(tabs, padding=8)
        self._add_tab(tabs, new, "Nové pojištění")
        self._add_tab(tabs, policies, "Moje smlouvy")
        self.tab_renewals = self._add_tab(tabs, offers, "Návrhy prodloužení")
        self.tab_claims = self._add_tab(tabs, claims, "Škodné události")
        self.customer_tabs = tabs
        self._build_policy_form(appearance.scroll_form(new))
        self._build_client_policies(policies)
        self._build_client_offers(offers)
        self._build_client_claims(claims)

    @staticmethod
    def _add_tab(notebook, frame, text):
        notebook.add(frame, text=text)
        return notebook.index(frame)

    def toggle_customer_field(self):
        if self.new_customer_visible:
            self.customer_name.pack_forget()
            self.new_button.config(text="+ Nový zákazník")
        else:
            self.customer_name.pack(side="left", padx=6)
            self.customer_name.focus_set()
            self.new_button.config(text="Zavřít")
        self.new_customer_visible = not self.new_customer_visible

    def _build_policy_form(self, parent):
        title = appearance.card_header(parent, "Sjednání nového pojištění", appearance.ACCENT)
        body = tk.Frame(parent, background=appearance.SURFACE)
        body.pack(fill="both", expand=True, padx=20, pady=(6, 10))
        body.pack(fill="both", expand=True)
        body.columnconfigure(0, weight=3)
        body.columnconfigure(1, weight=2)
        left = tk.Frame(body, background=appearance.SURFACE)
        left.grid(row=0, column=0, sticky="new")
        right = tk.Frame(body, background=appearance.SURFACE)
        right.grid(row=0, column=1, sticky="nsew", padx=(28, 0))

        products = tk.Frame(left, background=appearance.SURFACE)
        products.pack(fill="x")
        self.product = self.choose(products, "PRODUKT", 0)
        self.product.bind("<<ComboboxSelected>>", lambda _: self.variants())
        self.variant = self.choose(products, "VARIANTA", 1)
        self.variant.bind("<<ComboboxSelected>>", lambda _: self.variant_info())
        self.description = appearance.sink_text(left)
        self.subject_caption = tk.Label(left, text="SPZ / IDENTIFIKACE VOZIDLA",
                                        background=appearance.SURFACE, foreground=appearance.MUTED,
                                        font=("Segoe UI", 9, "bold"))
        self.subject_caption.pack(anchor="w", pady=(0, 4))
        self.subject = ttk.Entry(left)
        self.subject.pack(fill="x", pady=(0, 8))

        years = tk.Frame(left, background=appearance.SURFACE)
        years.pack(fill="x", pady=(0, 8))
        tk.Label(years, text="DÉLKA POJISTKY (ROKY)", background=appearance.SURFACE,
                 foreground=appearance.MUTED, font=("Segoe UI", 9, "bold")).pack(side="left")
        self.years = appearance.stepper(years, 1, 10, 1)
        value_age = tk.Frame(left, background=appearance.SURFACE)
        value_age.pack(fill="x", pady=(0, 8))
        tk.Label(value_age, text="HODNOTA VOZIDLA (KČ)", background=appearance.SURFACE,
                 foreground=appearance.MUTED, font=("Segoe UI", 9, "bold")).pack(side="left")
        self.value = ttk.Entry(value_age, width=14)
        self.value.insert(0, "300000")
        self.value.pack(side="left", padx=(10, 0))
        extras = tk.Frame(left, background=appearance.SURFACE)
        extras.pack(fill="x", pady=(0, 8))
        tk.Label(extras, text="VĚK POJISTNÍKA", background=appearance.SURFACE,
                 foreground=appearance.MUTED, font=("Segoe UI", 9, "bold")).pack(side="left")
        self.age = ttk.Entry(extras, width=8)
        self.age.insert(0, "30")
        self.age.pack(side="left", padx=(10, 20))
        tk.Label(extras, text="ROČNÍ NÁJEZD (KM)", background=appearance.SURFACE,
                 foreground=appearance.MUTED, font=("Segoe UI", 9, "bold")).pack(side="left")
        self.km = ttk.Entry(extras, width=10)
        self.km.insert(0, "10000")
        self.km.pack(side="left", padx=(10, 0))
        self.sec_auto = extras
        majetek = tk.Frame(left, background=appearance.SURFACE)
        majetek.pack(fill="x", pady=(0, 8))
        tk.Label(majetek, text="STÁŘÍ BUDOVY (ROKY)", background=appearance.SURFACE,
                 foreground=appearance.MUTED, font=("Segoe UI", 9, "bold")).pack(side="left")
        self.property_age = ttk.Entry(majetek, width=8)
        self.property_age.insert(0, "10")
        self.property_age.pack(side="left", padx=(10, 20))
        self.secured = tk.BooleanVar(value=True)
        ttk.Checkbutton(majetek, text="Objekt je zabezpečen (alarm, kamerový systém)",
                        variable=self.secured).pack(side="left", padx=(0, 10))
        self.sec_majetek = majetek
        bonus = tk.Frame(left, background=appearance.SURFACE)
        bonus.pack(fill="x")
        tk.Label(bonus, text="ROKY BEZ NEHODY (BONUS)", background=appearance.SURFACE,
                 foreground=appearance.MUTED, font=("Segoe UI", 9, "bold")).pack(side="left", padx=(0, 12))
        self.no_accident = appearance.stepper(bonus, 0, 30, 5)
        self.bonus_row = bonus
        actions = tk.Frame(right, background=appearance.SURFACE)
        actions.pack(fill="x", pady=(0, 8))
        self.outline(actions, "Spočítat nabídku", self.quote).pack(side="left", expand=True, fill="x")
        self.solid(actions, "Uzavřít pojištění", self.create_policy).pack(side="left", expand=True,
                                                                           fill="x", padx=(10, 0))
        self.quote_banner = tk.Frame(right, background=appearance.BANNER,
                                     highlightbackground=appearance.ACCENT, highlightthickness=2)
        self.quote_banner.pack(fill="x", pady=(0, 8))
        tk.Label(self.quote_banner, text="VÝSLEDEK KALKULACE", background=appearance.BANNER,
                 foreground=appearance.ACCENT, font=("Segoe UI", 9, "bold")).pack(anchor="w", padx=16, pady=(10, 2))
        self.quote_text = tk.Label(self.quote_banner, text="Cena se zobrazí po výpočtu nabídky.",
                                   background=appearance.BANNER, foreground=appearance.MUTED,
                                   font=("Segoe UI", 20, "bold"), anchor="w")
        self.quote_text.pack(fill="x", padx=16)
        self.quote_total = tk.Label(self.quote_banner, text="", background=appearance.BANNER,
                                    foreground=appearance.MUTED, font=("Segoe UI", 10), anchor="w")
        self.quote_total.pack(fill="x", padx=16, pady=(2, 6))
        self.quote_bar = ttk.Progressbar(self.quote_banner, mode="determinate", maximum=100)
        self.quote_bar.pack(fill="x", padx=16, pady=(0, 2))
        scale = tk.Frame(self.quote_banner, background=appearance.BANNER)
        scale.pack(fill="x", padx=16, pady=(2, 10))
        self.scale_from = tk.Label(scale, text="Základní sazba", background=appearance.BANNER,
                                   foreground=appearance.MUTED, font=("Segoe UI", 9))
        self.scale_from.pack(side="left")
        self.scale_to = tk.Label(scale, text="krytí", background=appearance.BANNER,
                                 foreground=appearance.MUTED, font=("Segoe UI", 9))
        self.scale_to.pack(side="right")
        coverage = tk.Frame(right, background=appearance.FIELD_BG, padx=16, pady=10)
        coverage.pack(fill="both", expand=False)
        self.coverage_title = tk.Label(coverage, text="ZAHRNUTÁ KRYTÍ", background=appearance.FIELD_BG,
                                       foreground=appearance.MUTED, font=("Segoe UI", 9, "bold"))
        self.coverage_title.pack(anchor="w", pady=(0, 6))
        self.coverage_rows = []
        for line in COVERAGE:
            for item in line:
                label = appearance.covered_row(coverage, item, appearance.FIELD_BG)
                label.pack(fill="x")
                self.coverage_rows.append(label)
        self.update_coverage()

    def choose(self, parent, caption, column):
        frame = tk.Frame(parent, background=appearance.SURFACE)
        frame.grid(row=0, column=column, sticky="ew", padx=(0, 8) if column == 0 else (8, 0))
        frame.columnconfigure(0, weight=1)
        tk.Label(frame, text=caption, background=appearance.SURFACE, foreground=appearance.MUTED,
                 font=("Segoe UI", 9, "bold")).pack(anchor="w")
        box = ttk.Combobox(frame, state="readonly", width=24)
        box.pack(fill="x", pady=(3, 0))
        return box

    def _build_client_policies(self, parent):
        card = appearance.card(parent, padding=12)
        header = appearance.card_header(card, "Moje smlouvy", appearance.ACCENT, count_text="0 smluv")
        self.client_policy_header = header.title_label
        self.client_policy_count = header.count_label
        self.client_policies = self.table(card, ("ID", "Předmět", "Druh", "Varianta", "Od",
                                                 "Konec krytí", "Kč/rok", "Kč celkem", "Stav"))
        line = self.row(card)
        self.outline(line, "Detail smlouvy", self.show_client_policy).pack(side="left")
        self.outline(line, "Ukončit smlouvu", self.cancel_policy, appearance.RED).pack(side="right")

    def _build_client_offers(self, parent):
        card = appearance.card(parent, padding=12)
        appearance.card_header(card, "Návrhy prodloužení smluv", appearance.AMBER)
        self.client_offers = self.table(card, ("ID", "Smlouva", "Vypočteno Kč/rok", "Schváleno Kč/rok",
                                               "Stav", "Nová smlouva"))
        line = self.row(card)
        self.solid(line, "Přijmout nabídku", self.accept_offer).pack(side="left")
        self.outline(line, "Odmítnout", self.decline_offer, appearance.RED).pack(side="right")
        tk.Label(card, text="Nabídku nejprve schvaluje správce. Při pozdním přijetí začíná nové "
                            "krytí dneškem.", foreground=appearance.MUTED).pack(anchor="w")

    def _build_client_claims(self, parent):
        report = appearance.card(parent, padding=12)
        appearance.card_header(report, "Nahlásit škodnou událost", appearance.RED)
        line = self.row(report)
        ttk.Label(line, text="SMLOUVA", style="Caps.TLabel").pack(side="left", padx=(0, 8))
        self.claim_policy = ttk.Combobox(line, state="readonly", width=28)
        self.claim_policy.pack(side="left", padx=(0, 8))
        self.occurred = self.field(line, "DATUM UDÁLOSTI", "", 13)
        self.damage = self.field(line, "VÝŠE ŠKODY (KČ)", "10000", 10)
        line = self.row(report)
        ttk.Label(line, text="POPIS UDÁLOSTI", style="Caps.TLabel").pack(side="left", padx=(0, 8))
        self.claim_description = ttk.Entry(line, width=44)
        self.claim_description.pack(side="left")
        self.solid(report, "Odeslat žádost o náhradu", self.submit_claim, appearance.RED).pack(fill="x", pady=(6, 0))
        history = appearance.card(parent, padding=12, pady=(8, 0))
        header = appearance.card_header(history, "Historie škodných událostí", appearance.RED, count_text="0 žádostí")
        self.claim_history_header = header.count_label
        self.client_claims = self.table(history, ("ID", "Smlouva", "Datum škody", "Škoda Kč",
                                                  "Výpočet Kč", "Stav", "Vyplaceno Kč", "Odůvodnění"))
        self.outline(history, "Detail žádosti", self.show_client_claim).pack(anchor="w")

    def build_admin(self, parent):
        tabs = ttk.Notebook(parent)
        tabs.pack(fill="both", expand=True)
        policies = ttk.Frame(tabs, padding=8)
        offers = ttk.Frame(tabs, padding=8)
        claims = ttk.Frame(tabs, padding=8)
        audit = ttk.Frame(tabs, padding=8)
        self._add_tab(tabs, policies, "Všechna pojištění")
        self.tab_ending = self._add_tab(tabs, offers, "Končící pojištění / ceny")
        self.tab_requests = self._add_tab(tabs, claims, "Žádosti / výplaty")
        self._add_tab(tabs, audit, "Audit")
        self.admin_tabs = tabs
        self._build_admin_policies(policies)
        self._build_admin_offers(offers)
        self._build_admin_claims(claims)
        self._build_admin_audit(audit)

    def _pills(self, parent):
        frame = tk.Frame(parent, background=appearance.SURFACE)
        frame.pack(side="right")
        self.status_pills = {}
        for key, label, text, background in (("active", "Aktivní", "#166534", "#dcfce7"),
                                             ("pending", "Čekající", "#92400e", "#fef3c7"),
                                             ("rejected", "Zamítnuto", "#991b1b", "#fee2e2"),
                                             ("closed", "Uzavřeno", "#4b5563", "#f3f4f6")):
            pill = tk.Frame(frame, background=background, padx=10, pady=4)
            pill.pack(side="left", padx=(0, 6))
            label_widget = tk.Label(pill, text=f"{label}: 0", background=background, foreground=text,
                                    font=("Segoe UI", 9, "bold"))
            label_widget.pack()
            self.status_pills[key] = label_widget
        return frame

    def _build_admin_policies(self, policies):
        card = appearance.card(policies, padding=12)
        header = appearance.card_header(card, "Všechna pojištění", appearance.ACCENT, count_text="0 smluv")
        self.admin_policy_summary = header.count_label
        self._pills(card)
        self.admin_policies = self.table(card, ("ID", "Klient", "Předmět", "Druh", "Varianta",
                                                "Od", "Do (mimo)", "Kč/rok", "Stav"))
        self.outline(card, "Detail smlouvy", self.show_admin_policy).pack(anchor="w", pady=(6, 0))

    def _build_admin_offers(self, offers):
        card = appearance.card(offers, padding=12)
        appearance.card_header(card, "Končící pojištění a návrhy cen", appearance.AMBER)
        self.admin_offers = self.table(card, ("ID", "Smlouva", "Předmět", "Konec smlouvy",
                                              "Vypočteno Kč/rok", "Schváleno Kč/rok", "Stav", "Nová smlouva"))
        line = self.row(card)
        self.override_price = self.field(line, "VLASTNÍ ROČNÍ CENA", "", 14)
        self.solid(line, "Potvrdit výpočet", self.approve_calculated_offer).pack(side="left", padx=8, anchor="s")
        self.outline(line, "Použít vlastní cenu", self.approve_custom_offer, appearance.AMBER).pack(side="left", anchor="s")
        tk.Label(card, text="Tabulka obsahuje návrhy pro končící/skončené smlouvy i historii "
                            "rozhodnutí. Přijaté návrhy se znovu nezpracovávají.",
                 foreground=appearance.MUTED).pack(anchor="w")

    def _build_admin_claims(self, claims):
        card = appearance.card(claims, padding=12)
        header = appearance.card_header(card, "Žádosti o náhradu škody", appearance.RED, count_text="Čekající: 0")
        self.claim_header_pending = header.count_label
        self.admin_claims = self.table(card, ("ID", "Smlouva", "Datum škody", "Škoda Kč",
                                              "Výpočet Kč", "Stav", "Vyplaceno Kč", "Odůvodnění"))
        line = self.row(card)
        self.note = self.field(line, "ODŮVODNĚNÍ", "Schváleno dle smlouvy", 32)
        self.solid(line, "Schválit a vyplatit", self.pay_claim).pack(side="left", padx=8, anchor="s")
        self.outline(line, "Zamítnout", self.reject_claim, appearance.RED).pack(side="left", anchor="s")

    def _build_admin_audit(self, audit):
        panel = tk.Frame(audit, background=appearance.HEADER)
        panel.pack(fill="both", expand=True)
        header = tk.Frame(panel, background=appearance.HEADER)
        header.pack(fill="x", padx=18, pady=(10, 6))
        appearance.dot(header, appearance.MINT)
        tk.Label(header, text="Auditní záznamy systému", background=appearance.HEADER,
                 foreground="white", font=("Segoe UI", 13, "bold")).pack(side="left")
        chip = tk.Frame(header, background="#2a4a46", padx=10, pady=4)
        chip.pack(side="right")
        self.audit_count = tk.Label(chip, text="0 ZÁZNAMŮ", background="#2a4a46",
                                    foreground=appearance.MINT, font=("Segoe UI", 9, "bold"))
        self.audit_count.pack()
        self.audit = appearance.text_area(panel)
        self.audit.configure(state="disabled")

    def show_client_policy(self):
        self.details("policies", self.client_policies)

    def show_admin_policy(self):
        self.details("policies", self.admin_policies)

    def show_client_claim(self):
        self.details("claims", self.client_claims)

    def show_admin_claim(self):
        self.details("claims", self.admin_claims)

    def accept_offer(self):
        self.post(f"/api/offers/{self.selected(self.client_offers)}/accept", {"customerId": self.customer_id()})

    def decline_offer(self):
        self.post(f"/api/offers/{self.selected(self.client_offers)}/decline", {"customerId": self.customer_id()})

    def approve_calculated_offer(self):
        self.approve_offer(False)

    def approve_custom_offer(self):
        self.approve_offer(True)

    def pay_claim(self):
        self.decide_claim(True)

    def reject_claim(self):
        self.decide_claim(False)

    def customer_id(self):
        if not self.customer.get():
            raise ValueError("Zvolte zákazníka.")
        return int(self.customer.get().split(":", 1)[0])

    def customer_label(self, ident):
        for customer in self.data["customers"]:
            if customer["id"] == ident:
                return customer["name"]
        return str(ident)

    def variants(self):
        index = self.product.current()
        if index < 0:
            return
        product = self.products[index]
        self.variant["values"] = [v["name"] for v in product["variants"]]
        self.variant.current(0)
        self.variant_info()

    def variant_info(self):
        if self.product.current() >= 0 and self.variant.current() >= 0:
            product = self.products[self.product.current()]
            variant = product["variants"][self.variant.current()]
            auto = product["id"] == "car"
            self.description.config(text=product["description"] + "\n" + variant["description"])
            self.quote_text.config(text="Pro aktuální údaje stiskněte Spočítat nabídku.", fg=appearance.MUTED)
            self.quote_total.config(text="")
            self.quote_bar["value"] = 0
            self.scale_to.config(text=f"{variant['name']} krytí")
            self.subject_caption.config(text="SPZ / IDENTIFIKACE VOZIDLA" if auto else "ADRESA / IDENTIFIKACE NEMOVITOSTI")
            for widget in (self.sec_auto, self.sec_majetek, self.bonus_row):
                widget.pack_forget()
            if auto:
                self.sec_auto.pack(fill="x", pady=(0, 8))
                self.bonus_row.pack(fill="x")
            else:
                self.sec_majetek.pack(fill="x", pady=(0, 8))
            self.update_coverage()

    def update_coverage(self):
        if self.product.current() >= 0 and self.variant.current() >= 0:
            variant = self.products[self.product.current()]["variants"][self.variant.current()]
            self.coverage_title.config(text=f"ZAHRNUTÁ KRYTÍ — {variant['name']}".upper())
            if "spoluúč" in variant["name"]:
                reach = 4
            elif "nájezd" in variant["name"]:
                reach = 5
            else:
                reach = 2
            for index, row in enumerate(self.coverage_rows):
                if index < reach:
                    row.pack(fill="x", pady=3)
                else:
                    row.pack_forget()

    def payload(self):
        if self.product.current() < 0 or self.variant.current() < 0:
            raise ValueError("Nejprve načtěte druhy pojištění ze serveru.")
        product = self.products[self.product.current()]
        return {"productId": product["id"], "variantId": product["variants"][self.variant.current()]["id"],
                "years": int(self.years.get()), "risk": {"insuredValue": float(self.value.get().replace(",", ".")),
                "age": int(self.age.get()), "annualKilometers": int(self.km.get()),
                "accidentFreeYears": int(self.no_accident.get()), "propertyAge": int(self.property_age.get()),
                "secured": self.secured.get()}}

    def quote(self):
        payload = self.payload()
        self.run(lambda: request("/api/quote", payload), self.show_quote)

    def show_quote(self, quote):
        annual = quote["annualPremium"]
        total = quote["totalPremium"]
        self.quote_text.config(text=f'Ročně: {annual:,.2f} Kč', fg=appearance.INK)
        self.quote_total.config(text=f'Celkem za {self.years.get()} let: {total:,.2f} Kč')
        self.quote_bar["value"] = min(100, annual / 15000 * 100)

    def create_policy(self):
        payload = self.payload() | {"customerId": self.customer_id(), "subject": self.subject.get()}
        self.post("/api/policies", payload)

    def register(self):
        name = self.customer_name.get()

        def registered(customer):
            self.customer.set(f'{customer["id"]}: {customer["name"]}')
            self.refresh()

        self.run(lambda: request("/api/customers", {"name": name}), registered)

    def cancel_policy(self):
        policy_id = self.selected(self.client_policies)
        if messagebox.askyesno("Ukončit?", f"Ukončit smlouvu #{policy_id} k dnešnímu simulačnímu dni?",
                               parent=self):
            self.post(f"/api/policies/{policy_id}/cancel", {"customerId": self.customer_id()})

    def submit_claim(self):
        if not self.claim_policy.get():
            raise ValueError("Zvolte smlouvu.")
        self.post("/api/claims", {"customerId": self.customer_id(),
                                  "policyId": int(self.claim_policy.get().split(":", 1)[0]),
                                  "occurredOn": self.occurred.get(),
                                  "description": self.claim_description.get(),
                                  "damage": float(self.damage.get().replace(",", "."))})

    def approve_offer(self, override):
        amount = float(self.override_price.get().replace(",", ".")) if override and self.override_price.get() else None
        self.post(f"/api/offers/{self.selected(self.admin_offers)}/approve", {"annualPremium": amount})

    def decide_claim(self, approve):
        self.post(f"/api/claims/{self.selected(self.admin_claims)}/decide",
                  {"approve": approve, "note": self.note.get()})

    def advance_time(self):
        try:
            months = int(self.sim_months.get())
        except ValueError as error:
            raise ValueError("Počet měsíců musí být celé číslo.") from error
        self.post("/api/time", {"months": months})

    def details(self, kind, tree):
        ident = self.selected(tree)
        item = next(x for x in self.data[kind] if x["id"] == ident)
        window = tk.Toplevel(self)
        window.title(f"Detail #{ident}")
        window.geometry("640x560")
        window.minsize(560, 440)
        window.configure(background=appearance.BACKGROUND)
        header = tk.Frame(window, background=appearance.HEADER, padx=20, pady=12)
        header.pack(fill="x")
        tk.Label(header, text=f"Detail #{ident}", background=appearance.HEADER, foreground="white",
                 font=("Segoe UI", 14, "bold")).pack(side="left")
        tk.Label(header, text="Detail pojistné smlouvy", background=appearance.HEADER,
                 foreground=appearance.MINT, font=("Segoe UI", 9, "bold")).pack(side="left", padx=(10, 0))
        body = tk.Frame(window, background=appearance.SURFACE)
        body.pack(fill="both", expand=True, padx=20, pady=12)
        for key, value in item.items():
            line = tk.Frame(body, background=appearance.SURFACE)
            line.pack(fill="x", pady=4)
            tk.Label(line, text=key.upper(), background=appearance.SURFACE, foreground=appearance.MUTED,
                     font=("Segoe UI", 9, "bold"), width=22, anchor="w").pack(side="left")
            tk.Label(line, text=str(value), background=appearance.SURFACE, foreground=appearance.INK,
                     anchor="w").pack(side="left")
        self.solid(body, "Zavřít", window.destroy).pack(fill="x", pady=(10, 0))

    def _maximize(self):
        screen_w = self.winfo_screenwidth()
        screen_h = self.winfo_screenheight()
        self.geometry(f"{screen_w}x{screen_h}")
        self.minsize(1160, 820)
        self.state("zoomed")

    def run(self, work, success):
        self.busy = True
        self.progress.start(12)
        self.status.set("Pracuji…")

        def worker():
            # сетевой вызов идёт на фоне, чтобы окно не замерзало; готовый результат
            # забирает poll() (тот же трюк, что в GUI UniRentu, obj-prog — достаточно
            # было переписать конечные точки)
            try:
                self.tasks.put((success, work(), None))
            except Exception as error:
                self.tasks.put((success, None, error))

        threading.Thread(target=worker, daemon=True).start()

    def poll(self):
        # в Tk можно заходить только из главного потока, поэтому здесь забираем то, что сделал worker
        try:
            callback, result, error = self.tasks.get_nowait()
            self.busy = False
            self.progress.stop()
            if error:
                self.status.set(f"Chyba: {error}")
                messagebox.showerror("Chyba", str(error), parent=self)
            else:
                self.status.set("Hotovo")
                self.guard(lambda: callback(result))
        except queue.Empty:
            pass
        self.after(100, self.poll)

    def post(self, path, data):
        # после записи обновляю всё состояние целиком, иначе таблицы станут вести себя так, будто ничего не изменилось
        self.run(lambda: request(path, data), lambda _: self.refresh())

    def refresh(self):
        self.run(lambda: (request("/api/state"), request("/api/products")), self.loaded)

    def loaded(self, result):
        data, products = result
        # если поле пустое или держит старую дату, подставляем сегодняшнюю — иначе всё
        # сгниёт при сдвиге времени
        if self.occurred.get() in ("", self.data["today"]):
            self.occurred.delete(0, "end")
            self.occurred.insert(0, data["today"])
        self.data = data
        self.data["policies"] = [p["contract"] | {"status": p["status"]} for p in data["policies"]]
        self.date.set("Datum: " + data["today"])
        old_customer = self.customer.get()
        self.customer["values"] = [f'{c["id"]}: {c["name"]}' for c in data["customers"]]
        if old_customer in self.customer["values"]:
            self.customer.set(old_customer)
        elif data["customers"]:
            self.customer.current(0)
        if products != self.products:
            self.products = products
            self.product["values"] = [p["name"] for p in products]
            if products:
                self.product.current(0)
                self.variants()
        self.render()
        self.status.set("Data aktualizována. Změny se ukládají automaticky.")

    @staticmethod
    def fill(tree, items, values):
        # удерживаю выделение, чтобы смотрящий на строку не терял её при обновлении
        selection = tree.selection()
        tree.delete(*tree.get_children())
        for index, item in enumerate(items):
            tags = ("even" if index % 2 == 0 else "odd", STATUS_TAGS.get(item.get("status"), ""))
            tree.insert("", "end", iid=str(item["id"]), values=values(item), tags=tags)
        if tree.get_children():
            tree.empty_label.place_forget()
        else:
            tree.empty_label.place(relx=0.5, rely=0.55, anchor="center")
        if selection and tree.exists(selection[0]):
            tree.selection_set(selection[0])

    def render(self):
        customer_id = self.customer_id() if self.customer.get() else None
        policies = self.data["policies"]
        self.policy_count.set(str(sum(p["status"] == "Active" for p in policies)))
        self.offer_count.set(str(sum(o["status"] in ("AwaitingAdmin", "AwaitingCustomer") for o in self.data["offers"])))
        self.claim_count.set(str(sum(c["status"] == "Pending" for c in self.data["claims"])))
        mine = [p for p in policies if p["customerId"] == customer_id]
        ids = {p["id"] for p in mine}
        self.fill(self.client_policies, mine, lambda p: (p["id"], p["subject"], p["productId"], p["variantId"],
                                                         p["startsOn"], p["endsOn"], p["annualPremium"],
                                                         p["totalPremium"], STATUS[p["status"]]))
        self.fill(self.admin_policies, policies, lambda p: (p["id"], self.customer_label(p["customerId"]),
                                                            p["subject"], p["productId"], p["variantId"],
                                                            p["startsOn"], p["endsOn"], p["annualPremium"],
                                                            STATUS[p["status"]]))
        offer_values = lambda o: (o["id"], o["policyId"],
                                  o["calculatedAnnualPremium"],
                                  o["approvedAnnualPremium"] if o["approvedAnnualPremium"] is not None else "—",
                                  STATUS[o["status"]], o["newPolicyId"] or "—")
        by_id = {p["id"]: p for p in policies}
        self.fill(self.admin_offers, self.data["offers"],
                  lambda o: (o["id"], o["policyId"], by_id[o["policyId"]]["subject"],
                             by_id[o["policyId"]]["endsOn"], *offer_values(o)[2:]))
        self.fill(self.client_offers, [o for o in self.data["offers"]
                                       if o["policyId"] in ids and o["status"] != "AwaitingAdmin"], offer_values)
        claim_values = lambda c: (c["id"], c["policyId"], c["occurredOn"], c["damage"],
                                  c["calculatedCompensation"], STATUS[c["status"]],
                                  c["paidAmount"], c["decisionNote"])
        self.fill(self.admin_claims, self.data["claims"], claim_values)
        self.fill(self.client_claims, [c for c in self.data["claims"] if c["policyId"] in ids], claim_values)
        selected = self.claim_policy.get()
        self.claim_policy["values"] = [f'{p["id"]}: {p["subject"]}' for p in mine]
        if selected in self.claim_policy["values"]:
            self.claim_policy.set(selected)
        elif mine:
            self.claim_policy.current(0)
        else:
            self.claim_policy.set("")
        self.audit.configure(state="normal")
        self.audit.delete("1.0", "end")
        for entry in self.data["audit"]:
            self.audit.insert("end", f'{entry["date"]} | {entry["action"]} | {entry["message"]}\n')
        self.audit.configure(state="disabled")

        self._render_shell(policies, mine)

    def _render_shell(self, policies, mine):
        name = self.customer.get().split(":", 1)[-1].strip() if self.customer.get() else ""
        self.client_policy_header.config(text=f"Moje smlouvy – {name}" if name else "Moje smlouvy")
        self.client_policy_count.config(text=f"{len(mine)} smluv")
        self.admin_policy_summary.config(text=f"{len(policies)} smluv celkem")
        counts = {"active": 0, "pending": 0, "rejected": 0, "closed": 0}
        for policy in policies:
            mapping = {"Active": "active", "Scheduled": "active", "AwaitingAdmin": "pending",
                       "AwaitingCustomer": "pending", "Pending": "pending", "Rejected": "rejected",
                       "Accepted": "active", "Cancelled": "closed", "Expired": "closed"}
            counts[mapping.get(policy["status"], "closed")] += 1
        labels = {"active": "Aktivní", "pending": "Čekající", "rejected": "Zamítnuto", "closed": "Uzavřeno"}
        for key, widget in self.status_pills.items():
            widget.config(text=f"{labels[key]}: {counts[key]}")
        pending_claims = sum(c["status"] == "Pending" for c in self.data["claims"])
        renewal_count = len(self.data["offers"])
        self.claim_header_pending.config(text=f"Čekající: {pending_claims}")
        self.claim_history_header.config(text=f"{len(self.data['claims'])} žádostí")
        self.customer_tabs.tab(self.tab_renewals, text="Návrhy prodloužení" + (f" ({renewal_count})" if renewal_count else ""))
        self.customer_tabs.tab(self.tab_claims, text="Škodné události" + (f" ({pending_claims})" if pending_claims else ""))
        self.admin_tabs.tab(self.tab_ending, text="Končící pojištění / ceny" + (f" ({renewal_count})" if renewal_count else ""))
        self.admin_tabs.tab(self.tab_requests, text="Žádosti / výplaty" + (f" ({pending_claims})" if pending_claims else ""))
        active_count = sum(p["status"] == "Active" for p in policies)
        premium = sum(p["annualPremium"] for p in policies if p["status"] == "Active")
        self.footer_active.config(text=f"{active_count} aktivních smluv")
        self.footer_premium.config(text=f"Celkem pojistné: {money(premium)}/rok")
        self.audit_count.config(text=f"{len(self.data['audit'])} ZÁZNAMŮ")

    def start_server(self):
        logger = self.logger.get()  # в Tk нельзя заходить из другого потока, поэтому забираем значение здесь

        def work():
            try:
                request("/api/health")
                return "Připojeno k běžícímu serveru; logger zůstává dle jeho konfigurace."
            except Exception:
                pass
            if BASE != "http://127.0.0.1:5090":
                raise RuntimeError("Pro vlastní INSURANCE_API_URL spusťte server samostatně.")
            if self.server is None or self.server.poll() is not None:
                path = ROOT / "data/python-launch.log"
                path.parent.mkdir(parents=True, exist_ok=True)
                if self.server_log:
                    self.server_log.close()
                self.server_log = path.open("a", encoding="utf-8")
                environment = os.environ.copy()
                environment["Insurance__Logger"] = logger
                self.server = subprocess.Popen(["dotnet", "run", "--project", "src/Insurance.App",
                                                "--no-launch-profile", "--", "--api"],
                                               cwd=ROOT, env=environment, stdout=self.server_log,
                                               stderr=subprocess.STDOUT)
            for _ in range(90):
                if self.server.poll() is not None:
                    break
                try:
                    request("/api/health")
                    return "C# server spuštěn."
                except Exception:
                    time.sleep(1)
            raise RuntimeError("Server nenaběhl. Viz data/python-launch.log; "
                               "nespouštějte konzoli nad stejnou databází současně.")

        def started(message):
            self.logger.configure(state="disabled")
            self.refresh()
            self.status.set(message)

        self.run(work, started)

    def close(self):
        if self.busy:
            messagebox.showinfo("Počkejte", "Počkejte na dokončení aktuální operace.", parent=self)
            return
        if self.server is not None and self.server.poll() is None:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(self.server.pid), "/T", "/F"], capture_output=True)
            else:
                self.server.terminate()
            self.server.wait(timeout=10)
        if self.server_log:
            self.server_log.close()
        self.destroy()


if __name__ == "__main__":
    App().mainloop()