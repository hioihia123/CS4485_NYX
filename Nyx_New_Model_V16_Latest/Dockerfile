FROM python:3.14-slim
WORKDIR /app
COPY requirements.txt /app/requirements.txt
RUN pip install --no-cache-dir -r requirements.txt && pip install --no-cache-dir scikit-learn==1.8.0
COPY . /app
CMD ["python", "train_nyx_model_v16.py"]
