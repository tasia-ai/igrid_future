FROM python:3.12-slim

WORKDIR /app

COPY tos_server.py .
COPY tos-accept.html .
COPY tos.md .

EXPOSE 8099

ENTRYPOINT ["python3", "tos_server.py"]
CMD ["--port", "8099", "--db", "/data/accesscontrol.db", "--file", "/app/tos.md"]
