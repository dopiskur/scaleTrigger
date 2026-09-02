"""
scaleTriggerLoad_locust.py

Locust load-test script for the ScaleTrigger API.

Sends POST /api/vote/add?option=yes|no, randomly choosing yes/no per
request. Works both for local runs (`locust -f ...`) and as a direct
upload to Azure Load Testing, which runs Locust scripts natively.

Authentication is auto-detected per simulated user via on_start(): GET
/api/auth/status is checked first, and if it reports authRequired=true,
the user logs in with admin:admin (override via
AUTH_USERNAME/AUTH_PASSWORD) and attaches the resulting JWT to every
subsequent vote.

Running locally (web UI):

    pip install locust
    locust -f scaleTriggerLoad_locust.py --host https://scaletrigger-api.azurewebsites.net

Then open http://localhost:8089 and set the number of users and spawn
rate - Locust's built-in ramp-up, equivalent to scaleTriggerLoad.py's
--ramp/--ramp-step/--ramp-interval.

Running locally (headless):

    locust -f scaleTriggerLoad_locust.py \
        --host https://scaletrigger-api.azurewebsites.net \
        --users 200 --spawn-rate 10 --run-time 5m --headless

Running on Azure Load Testing:

    Create a test in the Azure portal, choose "Locust" as the test type,
    and upload this file. Set host, user count, spawn rate and duration
    in the test configuration instead of on the command line.

Note: Locust ramps by concurrent simulated users, not a direct
requests-per-second target - with wait_time at 0, users roughly
correspond to in-flight requests, but tune --users/--spawn-rate against
the observed request rate rather than assuming a 1:1 mapping.

Environment variables:

    AUTH_USERNAME / AUTH_PASSWORD   Override the admin:admin default used for JWT login.
    INSECURE_TLS                    true/false (default false) - skip TLS certificate
                                     verification, for the self-signed cert the VM/VMSS demo
                                     scenarios serve over HTTPS (scaleTriggerLoad.py and
                                     Run-ScalingScenarios.ps1 handle the same certificate
                                     unconditionally, since they target VM/VMSS specifically;
                                     this script also runs against App Service/Container
                                     Apps/AKS/Azure Load Testing with a real certificate, so
                                     it's opt-in here instead - leave it false there).

    INSECURE_TLS=true locust -f scaleTriggerLoad_locust.py \
        --host https://vm-public-ip --users 200 --spawn-rate 10 --run-time 5m --headless
"""

import os
import random

from locust import HttpUser, task, between

AUTH_USERNAME = os.environ.get("AUTH_USERNAME", "admin")
AUTH_PASSWORD = os.environ.get("AUTH_PASSWORD", "admin")
INSECURE_TLS = os.environ.get("INSECURE_TLS", "false").lower() == "true"


class ScaleTriggerVoter(HttpUser):
    # No think time by default; set a real range (e.g. between(0.1, 0.5)) for human-like pacing.
    wait_time = between(0, 0)

    token = None

    def on_start(self):
        """Checks auth status once per user; logs in only if the API requires it."""
        if INSECURE_TLS:
            self.client.verify = False

        status = self.client.get("/api/auth/status", name="/api/auth/status")

        if status.json().get("authRequired"):
            login = self.client.post(
                "/api/auth/login",
                json={"username": AUTH_USERNAME, "password": AUTH_PASSWORD},
                name="/api/auth/login",
            )
            login.raise_for_status()
            self.token = login.json()["token"]

    @task
    def vote(self):
        option = random.choice(["yes", "no"])
        headers = {"Authorization": f"Bearer {self.token}"} if self.token else {}
        self.client.post(f"/api/vote/add?option={option}", headers=headers, name="/api/vote/add")
