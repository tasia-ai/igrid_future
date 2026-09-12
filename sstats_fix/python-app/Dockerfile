FROM python:3.12-slim

WORKDIR /app

# Install dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy app
COPY sstats_proxy.py .

EXPOSE 8080

# Same MySQL as OpenSim via env vars:
#   MYSQL_HOST, MYSQL_PORT, MYSQL_DB, MYSQL_USER, MYSQL_PASS
# Dashboard auth:
#   PROXY_USERNAME, PROXY_PASSWORD

CMD ["python", "sstats_proxy.py"]
